using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.AiBooking;

// One turn of the client-held transcript - see the class doc comment on ProposeBookingCommandRequest for
// why this is bounded and stateless rather than a persisted AI conversation.
public sealed record ProposeBookingTurnRequest(string Role, string Text);

// AI-assisted booking (WP-9) - see docs/ai-assisted-booking.md for the full design this implements.
// Deliberately named "Propose", not "Create": this NEVER creates a Booking, NotificationOutboxItem,
// ApprovalRequest, or any other persisted row, and never acquires IResourceBookingLock - it is a pure,
// read-only interpretation + preflight check, exactly as the task's "AI proposes, it never directly
// creates a booking" requirement demands. A successful result is a SNAPSHOT (see
// docs/bookings-and-concurrency.md, "read model, not a guarantee"), not a reservation - the frontend must
// call the EXISTING POST /api/bookings (CreateBookingCommandRequest) to actually create anything, which
// re-validates everything from scratch (including re-acquiring the real lock) and is the only code path
// that ever does.
//
// PriorTurns carries a short, client-held transcript (never persisted server-side - see "Avoid ...
// persistence of AI conversations unless there is a demonstrated need" in the task brief) so a
// clarification round-trip ("What date?" -> "next Tuesday") does not require the user to retype their
// entire original request - the frontend resends the ORIGINAL user turn plus every clarification
// question/answer since, bounded by MaxPriorTurns/MaxTurnTextLength below so a conversation can never
// grow into an unbounded, ever-more-expensive prompt.
public sealed record ProposeBookingCommandRequest(string Text, IReadOnlyList<ProposeBookingTurnRequest>? PriorTurns = null)
    : IRequest<ProposeBookingResponse>;

public enum ProposeBookingOutcome
{
    // A concrete, eligible candidate was found - Candidate is populated. Still just a snapshot; see the
    // class doc comment above.
    Proposed,

    // The request is understood well enough to know what's missing, but not well enough to propose a
    // candidate - Message is a specific, targeted question (in the caller's own language) the frontend
    // shows as the assistant's next turn. The frontend is expected to resend PriorTurns (including this
    // question and the user's answer) on the next call.
    NeedsClarification,

    // Every field needed to attempt a candidate was present and well-formed, but no real resource matches
    // (an invented/nonexistent resource type or name, a real resource whose capacity can't fit the
    // requested party size, or a real, matching resource that isn't actually available for the requested
    // window - outside its open hours, blacked out, or at capacity). Deliberately NOT NeedsClarification:
    // re-asking the same question would not change the answer; the request itself cannot succeed as
    // interpreted.
    NoSuitableResource,

    // AiBooking:Enabled is false - the feature is off. The interpreter is never called.
    Disabled,

    // The interpreter call failed, timed out, or returned something that could not be parsed at all - see
    // BookingIntentInterpretationException. Transient by nature; the frontend should let the user retry
    // or fall back to manual booking, never auto-retry on their behalf.
    ProviderUnavailable,
}

// TimeZoneId/ResourceCapacity/RequiresApproval are shown to the user BEFORE they confirm, per the task's
// "clearly display all booking fields before confirmation" requirement - Summary is a single, already-
// localized sentence (in Language) for a one-line review, built deterministically in C# (see
// BuildSummary), never a second model call. Confirming sends ResourceId/StartUtc/EndUtc/Quantity to the
// EXISTING POST /api/bookings exactly as manual booking does - this record intentionally has no
// "confirm token"/"proposal id" field, because there is nothing server-side to redeem: revalidation on
// confirm is simply CreateBookingCommandHandler running again from scratch.
public sealed record ProposeBookingCandidateResponse(
    Guid ResourceId,
    string ResourceName,
    string TimeZoneId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Quantity,
    int ResourceCapacity,
    bool RequiresApproval,
    string Summary);

public sealed record ProposeBookingResponse(ProposeBookingOutcome Outcome, string Language, string? Message, ProposeBookingCandidateResponse? Candidate);

public sealed class ProposeBookingCommandRequestValidator : AbstractValidator<ProposeBookingCommandRequest>
{
    public const int MaxTextLength = 500;
    public const int MaxPriorTurns = 6;

    public ProposeBookingCommandRequestValidator()
    {
        RuleFor(command => command.Text).NotEmpty().MaximumLength(MaxTextLength);
        RuleFor(command => command.PriorTurns!.Count).LessThanOrEqualTo(MaxPriorTurns).When(command => command.PriorTurns is not null);
        RuleForEach(command => command.PriorTurns).ChildRules(turn =>
        {
            turn.RuleFor(t => t.Role).Must(role => role is "user" or "assistant").WithMessage("Role must be 'user' or 'assistant'.");
            turn.RuleFor(t => t.Text).NotEmpty().MaximumLength(MaxTextLength);
        });
    }
}

// See docs/ai-assisted-booking.md for the full resolution pipeline this implements step by step. Every
// dependency below except IBookingIntentInterpreter/IOptions<AiBookingOptions>/TimeProvider is an
// EXISTING repository this feature deliberately reuses rather than duplicating - in particular,
// BookingEligibilityChecker (BookSpace.Application.Bookings, internal - accessible here because this is
// the same assembly) is the EXACT same read-only check CreateBookingCommandHandler itself calls, so a
// proposal and a real booking attempt can never silently disagree about what counts as available.
public sealed class ProposeBookingCommandHandler(
    IBookingIntentInterpreter bookingIntentInterpreter,
    IOptions<AiBookingOptions> aiBookingOptions,
    ITenantRepository tenantRepository,
    IResourceTypeRepository resourceTypeRepository,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    ICurrentUserContext currentUserContext,
    TimeProvider timeProvider,
    ILogger<ProposeBookingCommandHandler> logger)
    : IRequestHandler<ProposeBookingCommandRequest, ProposeBookingResponse>
{
    // A resource catalog large enough that a tenant would never plausibly need a second page for THIS
    // purpose (picking "the" meeting room/lab/desk by type+name+capacity) - unlike GetResourcesQueryRequest,
    // this never paginates further; a tenant with more Active resources than this is already far outside
    // what "ask in plain English which one you mean" scales to usefully anyway.
    private const int MaxCandidateResourcesConsidered = 200;
    private const int MaxReasonablePartySizeOrQuantity = 100_000;

    public async Task<ProposeBookingResponse> Handle(ProposeBookingCommandRequest request, CancellationToken cancellationToken)
    {
        var tenantId = TenantMembershipGuard.EnsureTenantMember(currentUserContext);
        var options = aiBookingOptions.Value;

        if (!options.Enabled)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.Disabled, BookingLanguageHeuristic.Detect(request.Text), DisabledMessage(BookingLanguageHeuristic.Detect(request.Text)), null);
        }

        var tenant = await tenantRepository.FindByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} for the current request was not found.");
        var referenceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(tenant.DefaultTimeZoneId);
        var referenceNowUtc = timeProvider.GetUtcNow();
        var referenceLocalNow = TimeZoneInfo.ConvertTime(referenceNowUtc, referenceTimeZone);

        var resourceTypes = await resourceTypeRepository.GetAllAsync(cancellationToken);
        var context = new BookingIntentContext(
            resourceTypes.Select(type => type.Name).ToList(),
            DateOnly.FromDateTime(referenceLocalNow.Date),
            TimeOnly.FromDateTime(referenceLocalNow.DateTime),
            tenant.DefaultTimeZoneId,
            referenceLocalNow.DayOfWeek);

        var priorTurns = (request.PriorTurns ?? [])
            .Select(turn => new BookingIntentTurn(turn.Role == "assistant" ? BookingIntentTurnRole.Assistant : BookingIntentTurnRole.User, turn.Text))
            .ToList();

        LlmBookingIntent intent;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            intent = await bookingIntentInterpreter.InterpretAsync(request.Text, priorTurns, context, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own bounded timeout fired, not the caller's own cancellation - see docs/ai-assisted-
            // booking.md ("Bounded behavior on failure"). Logs the technical outcome only, never the
            // prompt text - see the class doc comment and IBookingIntentInterpreter's own.
            logger.LogWarning("AI booking interpretation timed out after {TimeoutSeconds}s.", options.RequestTimeoutSeconds);
            var language = BookingLanguageHeuristic.Detect(request.Text);
            return new ProposeBookingResponse(ProposeBookingOutcome.ProviderUnavailable, language, ProviderUnavailableMessage(language), null);
        }
        catch (BookingIntentInterpretationException exception)
        {
            logger.LogWarning(exception, "AI booking interpretation failed.");
            var language = BookingLanguageHeuristic.Detect(request.Text);
            return new ProposeBookingResponse(ProposeBookingOutcome.ProviderUnavailable, language, ProviderUnavailableMessage(language), null);
        }

        var resolvedLanguage = intent.Language is "bs" or "en" ? intent.Language : BookingLanguageHeuristic.Detect(request.Text);

        // Date/quantity/resource are resolved UNCONDITIONALLY here, from the model's raw output fields,
        // regardless of intent.Status - even a model that set status="needs_clarification" (most often to
        // ask for a missing time) typically still fills in whatever else it DID understand (resourceTypeHint,
        // date, ...) in the very same response. This is deliberately done BEFORE the model's own
        // clarification question is honored below, so the backend's own day-wide-availability check (right
        // after) can override it - see that check's own doc comment for the real-world bug this fixes: a
        // model that proactively asks "what time?" would otherwise short-circuit this method via the
        // intent.Status check a few lines down, and the day-wide rejection below would never be reached at
        // all for that (extremely common) case.
        var dateResolution = ResolveDate(intent, referenceLocalNow, resolvedLanguage);
        var quantityResolution = ResolveQuantity(intent, resolvedLanguage);

        var candidateResolution = CandidateResolution.None;
        if (dateResolution.Date is not null && quantityResolution.ClarificationMessage is null)
        {
            var allActiveResourcesForEarlyResolution = (await resourceRepository.GetPagedAsync(
                    1, MaxCandidateResourcesConsidered, null, ResourceStatus.Active, cancellationToken))
                .Items;
            candidateResolution = ResolveResourceCandidates(
                intent, resourceTypes, allActiveResourcesForEarlyResolution, quantityResolution.ExplicitQuantityGiven, resolvedLanguage);
        }

        // Fetched once, as soon as both are known, and reused for the day-wide check immediately below AND
        // for the final BookingEligibilityChecker.CheckAsync call much further down (which already accepts
        // pre-loaded rules/blackouts for exactly this reason; see its own doc comment) - never fetched a
        // second time for the same resource.
        IReadOnlyList<Domain.Entities.AvailabilityRule> rules = [];
        IReadOnlyList<Domain.Entities.BlackoutPeriod> blackouts = [];
        TimeZoneInfo? resourceTimeZone = null;

        if (dateResolution.Date is { } earlyDate && candidateResolution.Resource is { } earlyResource)
        {
            resourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(earlyResource.TimeZoneId);
            rules = await availabilityRuleRepository.GetByResourceIdAsync(earlyResource.Id, cancellationToken);
            blackouts = await blackoutPeriodRepository.GetByResourceIdAsync(earlyResource.Id, cancellationToken);

            // Checked UNCONDITIONALLY as soon as the date and the resource are both known - deliberately
            // BEFORE the model's own clarification question is honored below (see this method's own doc
            // comment just above for the real-world bug that requires this exact ordering), and regardless
            // of whether a start time happens to already be given too: across a multi-round clarification
            // conversation, the model can easily end up filling in a start time (asked about, and answered,
            // in an EARLIER round) before the resource itself finally resolves to exactly one match (e.g.
            // "which room?" was answered last) - gating this on "no start time yet" would then never fire
            // for that ordering either. If NO AvailabilityRule covers this calendar date's day of week at
            // all, the resource is closed the ENTIRE day regardless of what time the user gives - no amount
            // of further clarification could ever produce a workable answer, so this is NoSuitableResource
            // (a request that cannot succeed as interpreted), not NeedsClarification, exactly the same
            // distinction the class doc comment on ProposeBookingOutcome.NoSuitableResource already draws
            // for every other "no real resource matches" case. Reuses
            // AvailabilityCalculator.ComputeOpenPeriodsUtc - the EXACT same function BookingEligibilityChecker
            // itself calls below - so this can never disagree with the real eligibility check about which
            // days are open. Deliberately does NOT also look at `blackouts` here: a full-day blackout is a
            // different concept ("unavailable", not "outside working hours") and the full eligibility check
            // further down is still the right place to catch it (it needs the actual requested window, which
            // a blackout period is compared against).
            var openPeriodsThatDay = AvailabilityCalculator.ComputeOpenPeriodsUtc(rules, earlyDate, earlyDate, resourceTimeZone);
            if (openPeriodsThatDay.Count == 0)
            {
                logger.LogInformation("AI booking candidate rejected - the resource has no AvailabilityRule covering {DayOfWeek}.", earlyDate.DayOfWeek);
                return new ProposeBookingResponse(ProposeBookingOutcome.NoSuitableResource, resolvedLanguage, ClosedOnThatDateMessage(resolvedLanguage), null);
            }
        }

        // The model's OWN clarification request is honored directly only when it actually supplied a
        // question - an empty/missing one is treated as malformed output (see the class doc comment on
        // LlmBookingIntent) and falls through to the backend's own field-by-field checks below instead of
        // being trusted at face value. Only reached once the day-wide check above has already ruled out the
        // one case where honoring it would be actively counterproductive - see this method's own doc
        // comment further up.
        if (intent.Status == "needs_clarification" && !string.IsNullOrWhiteSpace(intent.ClarificationQuestion))
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, intent.ClarificationQuestion, null);
        }

        if (dateResolution.ClarificationMessage is not null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, dateResolution.ClarificationMessage, null);
        }

        var date = dateResolution.Date!.Value;

        if (quantityResolution.ClarificationMessage is not null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, quantityResolution.ClarificationMessage, null);
        }

        if (candidateResolution.ClarificationMessage is not null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, candidateResolution.ClarificationMessage, null);
        }

        if (candidateResolution.Resource is null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NoSuitableResource, resolvedLanguage, NoSuitableResourceMessage(resolvedLanguage), null);
        }

        var resource = candidateResolution.Resource;

        // The reference phrase was interpreted using the TENANT's default zone (see BookingIntentContext
        // above); the matched resource may use a different zone. See docs/ai-assisted-booking.md
        // ("Reference time zone and cross-zone ambiguity") for why this check exists and why it is
        // deliberately narrow: only a RELATIVE expression ("next Tuesday") can actually mean a different
        // calendar date depending which zone it's resolved in - an explicit absolute date the model
        // already normalized is unaffected by which zone "today" was computed in. This no longer depends
        // on StartTime/EndTime being resolved yet - DateIsRelativeExpression is already known from the
        // model's own output regardless.
        if (resource.TimeZoneId != tenant.DefaultTimeZoneId && intent.DateIsRelativeExpression != false)
        {
            return new ProposeBookingResponse(
                ProposeBookingOutcome.NeedsClarification, resolvedLanguage, CrossZoneClarificationMessage(resolvedLanguage, resource.TimeZoneId), null);
        }

        // resourceTimeZone/rules/blackouts were already resolved above, in the SAME "date+resource both
        // known" branch that just ran the day-wide check against this exact resource - never re-fetched.
        resourceTimeZone ??= TimeZoneInfo.FindSystemTimeZoneById(resource.TimeZoneId);

        var startTime = TryParseStartTime(intent);
        if (startTime is null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, MissingStartTimeMessage(resolvedLanguage), null);
        }

        var endTimeResolution = ResolveEndTime(intent, startTime.Value, resolvedLanguage);
        if (endTimeResolution.ClarificationMessage is not null)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, endTimeResolution.ClarificationMessage, null);
        }

        var startUtc = AvailabilityCalculator.ConvertLocalToUtc(date, startTime.Value, resourceTimeZone);
        var endUtc = AvailabilityCalculator.ConvertLocalToUtc(date, endTimeResolution.EndTime!.Value, resourceTimeZone);

        // Re-checked here, in the resource's OWN zone, not just in the reference zone above - a window
        // that looked safely in the future when interpreted against the tenant's default zone could still
        // land in the past once re-resolved against a materially different resource zone. This, the
        // ordering check, and the UTC conversion itself are the authoritative checks this task requires -
        // never the model's own arithmetic.
        if (startUtc < referenceNowUtc)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, PastDateMessage(resolvedLanguage), null);
        }

        if (endUtc <= startUtc)
        {
            return new ProposeBookingResponse(ProposeBookingOutcome.NeedsClarification, resolvedLanguage, OrderingMessage(resolvedLanguage), null);
        }

        var eligibility = await BookingEligibilityChecker.CheckAsync(
            resource, startUtc, endUtc, quantityResolution.Quantity, excludeBookingId: null, rules, blackouts, bookingAvailabilityRepository, cancellationToken);

        if (eligibility != BookingEligibility.Eligible)
        {
            logger.LogInformation("AI booking candidate rejected by BookingEligibilityChecker: {Eligibility}.", eligibility);
            return new ProposeBookingResponse(ProposeBookingOutcome.NoSuitableResource, resolvedLanguage, EligibilityMessage(eligibility, resolvedLanguage), null);
        }

        var summary = BuildSummary(resource.Name, date, startTime.Value, endTimeResolution.EndTime.Value, resource.TimeZoneId, quantityResolution.Quantity, resolvedLanguage);
        var candidate = new ProposeBookingCandidateResponse(
            resource.Id, resource.Name, resource.TimeZoneId, startUtc, endUtc, quantityResolution.Quantity, resource.Capacity, resource.RequiresApproval, summary);

        return new ProposeBookingResponse(ProposeBookingOutcome.Proposed, resolvedLanguage, null, candidate);
    }

    private sealed record DateResolution(DateOnly? Date, string? ClarificationMessage)
    {
        public static DateResolution Clarify(string message) => new(null, message);
    }

    // Independently re-validates the date field BookingEligibilityChecker/AvailabilityCalculator would
    // otherwise be handed blindly - see "The backend must authoritatively verify date interpretation...,
    // past dates" in the task brief. Only the CALENDAR DATE itself is taken from the model (as a plain
    // string, strictly parsed here); split out from time resolution (see ResolveEndTime/TryParseStartTime
    // below) specifically so the resource can be resolved, and its day-wide availability checked, as soon
    // as the date alone is known - before a start time is even required.
    private static DateResolution ResolveDate(LlmBookingIntent intent, DateTimeOffset referenceLocalNow, string language)
    {
        if (string.IsNullOrWhiteSpace(intent.Date) || !DateOnly.TryParseExact(intent.Date, "yyyy-MM-dd", out var date))
        {
            return DateResolution.Clarify(MissingDateMessage(language));
        }

        if (date < DateOnly.FromDateTime(referenceLocalNow.Date))
        {
            return DateResolution.Clarify(PastDateMessage(language));
        }

        return new DateResolution(date, null);
    }

    private static TimeOnly? TryParseStartTime(LlmBookingIntent intent) =>
        !string.IsNullOrWhiteSpace(intent.StartTime) && TimeOnly.TryParseExact(intent.StartTime, "HH:mm", out var parsedStart) ? parsedStart : null;

    private sealed record EndTimeResolution(TimeOnly? EndTime, string? ClarificationMessage)
    {
        public static EndTimeResolution Clarify(string message) => new(null, message);
    }

    // Only reachable once a StartTime is already known (see TryParseStartTime's call site) - the actual
    // UTC conversion, DST handling, and past-date check still happen entirely in the caller, using
    // AvailabilityCalculator.ConvertLocalToUtc - the exact same function every other booking/availability
    // path in this codebase uses, never a second, divergent one.
    private static EndTimeResolution ResolveEndTime(LlmBookingIntent intent, TimeOnly startTime, string language)
    {
        TimeOnly? endTime = !string.IsNullOrWhiteSpace(intent.EndTime) && TimeOnly.TryParseExact(intent.EndTime, "HH:mm", out var parsedEnd)
            ? parsedEnd
            : null;

        if (endTime is null)
        {
            if (intent.DurationMinutes is not (> 0 and <= 24 * 60))
            {
                return EndTimeResolution.Clarify(MissingDurationMessage(language));
            }

            endTime = startTime.AddMinutes(intent.DurationMinutes.Value);
            // A duration that pushes past midnight wraps TimeOnly back to an earlier clock time (e.g.
            // 23:00 + 180 minutes = 02:00) rather than throwing - this codebase has no cross-midnight
            // booking concept anywhere else (every other booking window is a plain DateTimeOffset range
            // that never needs to "wrap"), so treating that as unresolved rather than silently picking a
            // wrong day is the safe choice here.
            if (endTime <= startTime)
            {
                return EndTimeResolution.Clarify(MissingDurationMessage(language));
            }
        }

        if (endTime <= startTime)
        {
            return EndTimeResolution.Clarify(OrderingMessage(language));
        }

        return new EndTimeResolution(endTime.Value, null);
    }

    private sealed record QuantityResolution(int Quantity, bool ExplicitQuantityGiven, string? ClarificationMessage)
    {
        public static QuantityResolution Clarify(string message) => new(0, false, message);
    }

    // See the class doc comment on ProposeBookingCommandHandler and docs/ai-assisted-booking.md
    // ("Quantity vs. party size") for the policy this encodes: an explicit Quantity (e.g. "book 2 desks")
    // is used exactly as given; a bare PartySize ("for 3 people") does NOT become Quantity=3 - it defaults
    // Quantity to 1 (book the one resource) and instead narrows WHICH resource is suitable via its
    // Capacity (see ResolveResourceCandidates) - deliberately never assumed to mean "reserve N units" the
    // way this codebase's existing Quantity field was built for shared/pooled resources.
    private static QuantityResolution ResolveQuantity(LlmBookingIntent intent, string language)
    {
        if (intent.Quantity is { } explicitQuantity)
        {
            if (explicitQuantity is <= 0 or > MaxReasonablePartySizeOrQuantity)
            {
                return QuantityResolution.Clarify(InvalidQuantityMessage(language));
            }

            return new QuantityResolution(explicitQuantity, true, null);
        }

        if (intent.PartySize is { } partySize && partySize is <= 0 or > MaxReasonablePartySizeOrQuantity)
        {
            return QuantityResolution.Clarify(InvalidQuantityMessage(language));
        }

        return new QuantityResolution(1, false, null);
    }

    private sealed record CandidateResolution(Domain.Entities.Resource? Resource, string? ClarificationMessage)
    {
        public static CandidateResolution Clarify(string message) => new(null, message);

        public static readonly CandidateResolution None = new(null, null);
    }

    // Never trusts ResourceTypeHint/ResourceNameHint as already-resolved identifiers - both are free text
    // the model extracted from the user's own words, matched here against this tenant's REAL catalog
    // (loaded through the tenant-filtered repositories, deriving TenantId exclusively from
    // ICurrentUserContext - see "Derive TenantId...exclusively from the authenticated context" in the
    // task brief). A hint that matches nothing real is never silently widened back to "search everything"
    // - that would let an invented resource type/name still accidentally resolve to an unrelated real
    // resource; it short-circuits to "no match" instead (CandidateResolution.None, mapped by the caller to
    // NoSuitableResource).
    private static CandidateResolution ResolveResourceCandidates(
        LlmBookingIntent intent,
        IReadOnlyList<Domain.Entities.ResourceType> resourceTypes,
        IReadOnlyList<Domain.Entities.Resource> activeResources,
        bool explicitQuantityGiven,
        string language)
    {
        var candidates = activeResources;

        if (!string.IsNullOrWhiteSpace(intent.ResourceTypeHint))
        {
            var matchedTypeIds = resourceTypes
                .Where(type => TextNormalization.FoldedContains(type.Name, intent.ResourceTypeHint) || TextNormalization.FoldedContains(intent.ResourceTypeHint, type.Name))
                .Select(type => type.Id)
                .ToHashSet();
            if (matchedTypeIds.Count == 0)
            {
                return CandidateResolution.None;
            }

            candidates = candidates.Where(resource => matchedTypeIds.Contains(resource.ResourceTypeId)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(intent.ResourceNameHint))
        {
            candidates = candidates.Where(resource => TextNormalization.FoldedContains(resource.Name, intent.ResourceNameHint)).ToList();
        }

        // Only applied when Quantity was NOT given explicitly - see ResolveQuantity's own doc comment.
        if (!explicitQuantityGiven && intent.PartySize is { } partySize && partySize > 0)
        {
            candidates = candidates.Where(resource => resource.Capacity >= partySize).ToList();
        }

        return candidates.Count switch
        {
            0 => CandidateResolution.None,
            1 => new CandidateResolution(candidates[0], null),
            _ => CandidateResolution.Clarify(MultipleCandidatesMessage(candidates.Select(resource => resource.Name).ToList(), language)),
        };
    }

    private static string BuildSummary(string resourceName, DateOnly date, TimeOnly startTime, TimeOnly endTime, string timeZoneId, int quantity, string language)
    {
        var dateText = date.ToString("yyyy-MM-dd");
        var timeText = $"{startTime:HH\\:mm}–{endTime:HH\\:mm}";
        return language == "bs"
            ? $"{resourceName}, {dateText}, {timeText} ({timeZoneId}), za {quantity} {(quantity == 1 ? "rezervaciju" : "jedinica")}."
            : $"{resourceName}, {dateText}, {timeText} ({timeZoneId}), quantity {quantity}.";
    }

    private static string EligibilityMessage(BookingEligibility eligibility, string language) => (eligibility, language) switch
    {
        (BookingEligibility.OutsideAvailability, "bs") => "Traženo vrijeme je izvan radnog vremena ovog resursa.",
        (BookingEligibility.OutsideAvailability, _) => "The requested time is outside this resource's availability.",
        (BookingEligibility.BlackoutConflict, "bs") => "Traženo vrijeme se poklapa s periodom nedostupnosti ovog resursa.",
        (BookingEligibility.BlackoutConflict, _) => "The requested time overlaps a blackout period for this resource.",
        (BookingEligibility.CapacityExceeded, "bs") => "Za traženo vrijeme nema dovoljno slobodnog kapaciteta.",
        (BookingEligibility.CapacityExceeded, _) => "There isn't enough remaining capacity for the requested time.",
        (BookingEligibility.ResourceUnavailable, "bs") => "Ovaj resurs trenutno nije dostupan za rezervaciju.",
        (BookingEligibility.ResourceUnavailable, _) => "This resource is not available for booking right now.",
        _ => NoSuitableResourceMessage(language),
    };

    private static string DisabledMessage(string language) => language == "bs"
        ? "AI asistent za rezervacije trenutno nije dostupan. Molimo koristite ručnu rezervaciju."
        : "AI-assisted booking isn't available right now. Please use manual booking instead.";

    private static string ProviderUnavailableMessage(string language) => language == "bs"
        ? "AI asistent trenutno ne odgovara. Pokušajte ponovo ili koristite ručnu rezervaciju."
        : "The AI assistant is temporarily unavailable. Please try again, or use manual booking.";

    private static string NoSuitableResourceMessage(string language) => language == "bs"
        ? "Nisam pronašao odgovarajući resurs za ovaj zahtjev. Pokušajte preformulisati ili koristite ručnu rezervaciju."
        : "I couldn't find a suitable resource for that request. Try rephrasing, or use manual booking.";

    private static string MissingDateMessage(string language) => language == "bs" ? "Za koji datum vam je potrebna rezervacija?" : "What date would you like?";

    // Deliberately distinct from EligibilityMessage's OutsideAvailability text ("Traženo vrijeme je izvan
    // radnog vremena" / "The requested time is outside...") - no specific time was ever requested here,
    // so saying the whole DAY is outside the resource's working hours is the accurate claim.
    private static string ClosedOnThatDateMessage(string language) => language == "bs"
        ? "Ovaj resurs ne radi tog dana - izvan je njegovog radnog vremena, bez obzira na vrijeme. Pokušajte drugi datum ili resurs."
        : "This resource isn't open that day - it's outside its working hours regardless of time. Try a different date or resource.";

    private static string MissingStartTimeMessage(string language) => language == "bs" ? "U koje vrijeme želite početi?" : "What time would you like to start?";

    private static string MissingDurationMessage(string language) => language == "bs" ? "Koliko dugo vam je to potrebno (ili u koje vrijeme završava)?" : "How long do you need it for (or what time does it end)?";

    private static string PastDateMessage(string language) => language == "bs" ? "Taj datum/vrijeme je već prošao. Odaberite budući termin." : "That date/time has already passed. Please choose a future time.";

    private static string OrderingMessage(string language) => language == "bs" ? "Vrijeme završetka mora biti poslije vremena početka." : "The end time must be after the start time.";

    private static string InvalidQuantityMessage(string language) => language == "bs" ? "Koliko jedinica/osoba je potrebno (molimo navedite razuman broj)?" : "How many people/units do you need (please give a reasonable number)?";

    private static string CrossZoneClarificationMessage(string language, string resourceTimeZoneId) => language == "bs"
        ? $"Pronađeni resurs se nalazi u vremenskoj zoni {resourceTimeZoneId}, drugačijoj od podrazumijevane zone vaše organizacije. Možete li potvrditi tačan datum/vrijeme za tu lokaciju?"
        : $"The matching resource is in the {resourceTimeZoneId} time zone, different from your organization's default. Could you confirm the exact date/time for that location?";

    private static string MultipleCandidatesMessage(IReadOnlyList<string> resourceNames, string language)
    {
        var names = string.Join(", ", resourceNames);
        return language == "bs" ? $"Pronašao sam više odgovarajućih resursa: {names}. Koji od njih želite?" : $"I found multiple matching resources: {names}. Which one would you like?";
    }
}
