using BookSpace.Application.AiBooking;
using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.AiBooking;

// Covers the task's own acceptance criteria #1, #3, #5, #6, #7, #8, #9 at the handler/unit level (mocked
// repositories, FakeBookingIntentInterpreter - never a real HTTP call, matching "CI must not call a real
// paid LLM API"). #2 and #4 (confirmation uses the real CreateBooking flow; a slot taken in between is
// rejected) and the cross-tenant case in #5 are proven at the integration level instead - see
// BookSpace.Api.Tests/AiBooking/AiBookingsEndpointsTests.cs - since they need the REAL CreateBookingCommandHandler
// and a real database to mean anything.
//
// Structural proof that a proposal can never create anything (the other half of #1/#3): this handler's
// constructor has no IBookingRepository, INotificationOutboxWriter, IResourceBookingLock, or
// IApprovalRequestRepository dependency at all - there is no code path through which Handle() could
// possibly persist a Booking/ApprovalRequest/NotificationOutboxItem even if every check below were
// skipped. No "verify AddAsync was never called" test is needed for a method that was never given a way
// to call it.
public sealed class ProposeBookingCommandHandlerTests
{
    private readonly FakeBookingIntentInterpreter _interpreter = new();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly Mock<IResourceTypeRepository> _resourceTypeRepository = new();
    private readonly Mock<IResourceRepository> _resourceRepository = new();
    private readonly Mock<IAvailabilityRuleRepository> _availabilityRuleRepository = new();
    private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();
    private readonly Mock<IBookingAvailabilityRepository> _bookingAvailabilityRepository = new();
    private readonly Mock<ICurrentUserContext> _currentUserContext = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ResourceTypeId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();

    // A Monday, arbitrarily - the handler-level tests never rely on "today"'s actual day-of-week meaning
    // anything (that's the model's job, not this handler's) - they only check the BACKEND's own
    // past/ordering/DST validation of an already-resolved date/time pair.
    private static readonly DateTimeOffset ReferenceNowUtc = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _timeProvider = new(ReferenceNowUtc);

    private AiBookingOptions _options = new() { Enabled = true, ApiKey = "test-key" };

    public ProposeBookingCommandHandlerTests()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns(TenantId);
        _currentUserContext.SetupGet(c => c.UserId).Returns(UserId);
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = TenantId, Name = "Acme", DefaultTimeZoneId = "UTC", CreatedAtUtc = ReferenceNowUtc });
        _resourceTypeRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceType>)[new ResourceType { Id = ResourceTypeId, TenantId = TenantId, Name = "Meeting Room" }]);
    }

    private ProposeBookingCommandHandler CreateSut() => new(
        _interpreter,
        Options.Create(_options),
        _tenantRepository.Object,
        _resourceTypeRepository.Object,
        _resourceRepository.Object,
        _availabilityRuleRepository.Object,
        _blackoutPeriodRepository.Object,
        _bookingAvailabilityRepository.Object,
        _currentUserContext.Object,
        _timeProvider,
        NullLogger<ProposeBookingCommandHandler>.Instance);

    private static Resource CreateResource(int capacity = 8, string timeZoneId = "UTC", Guid? id = null) => new()
    {
        Id = id ?? ResourceId, TenantId = TenantId, ResourceTypeId = ResourceTypeId, Name = "Conference Room",
        Capacity = capacity, Status = ResourceStatus.Active, TimeZoneId = timeZoneId, RequiresApproval = false,
    };

    // Open every day, all day, in the resource's own zone - the handler-level tests below are about
    // field validation and resource resolution, not about exercising AvailabilityCalculator's own
    // day-of-week/DST logic (already covered by availability-and-timezones.md's own suite and by
    // DeterministicDstCoverage below).
    private static AvailabilityRule OpenAllDay(Resource resource, DayOfWeek dayOfWeek) => new()
    {
        Id = Guid.NewGuid(), TenantId = resource.TenantId, ResourceId = resource.Id,
        DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
    };

    private void SetupResourcesAndAvailability(IReadOnlyList<Resource> resources, Resource? openResource = null, DayOfWeek? openDay = null)
    {
        _resourceRepository
            .Setup(r => r.GetPagedAsync(1, It.IsAny<int>(), null, ResourceStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Resource>(resources, 1, resources.Count, resources.Count));

        foreach (var resource in resources)
        {
            var rules = openResource is not null && resource.Id == openResource.Id && openDay is { } day
                ? (IReadOnlyList<AvailabilityRule>)[OpenAllDay(resource, day)]
                : [];
            _availabilityRuleRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rules);
            _blackoutPeriodRepository.Setup(r => r.GetByResourceIdAsync(resource.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            _bookingAvailabilityRepository
                .Setup(r => r.GetActiveBookingsAsync(resource.Id, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }
    }

    // The date a well-formed test intent targets - tomorrow relative to ReferenceNowUtc, in UTC (the
    // tenant's own DefaultTimeZoneId here), comfortably in the future and with a known DayOfWeek for the
    // matching AvailabilityRule.
    private static readonly DateOnly TargetDate = DateOnly.FromDateTime(ReferenceNowUtc.AddDays(1).Date);

    private static LlmBookingIntent ValidIntent(
        string language = "en", string? resourceTypeHint = "meeting room", string? resourceNameHint = null,
        int? partySize = 3, int? quantity = null, string? date = null, string? startTime = "14:00", string? endTime = "16:00",
        int? durationMinutes = null, bool? dateIsRelative = true) =>
        new(language, "ok", null, resourceTypeHint, resourceNameHint, partySize, quantity,
            date ?? TargetDate.ToString("yyyy-MM-dd"), dateIsRelative, startTime, endTime, durationMinutes);

    [Fact]
    public async Task Handle_WithAValidRequest_ReturnsAProposedCandidate()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent();
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("Book a meeting room for 3 people tomorrow from 2 to 4 PM"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
        Assert.NotNull(result.Candidate);
        Assert.Equal(resource.Id, result.Candidate!.ResourceId);
        Assert.Equal(1, result.Candidate.Quantity); // partySize alone never becomes Quantity - see ResolveQuantity.
        Assert.Equal(new DateTimeOffset(TargetDate.Year, TargetDate.Month, TargetDate.Day, 14, 0, 0, TimeSpan.Zero), result.Candidate.StartUtc);
        Assert.Equal(new DateTimeOffset(TargetDate.Year, TargetDate.Month, TargetDate.Day, 16, 0, 0, TimeSpan.Zero), result.Candidate.EndUtc);
        Assert.False(string.IsNullOrWhiteSpace(result.Candidate.Summary));
    }

    [Fact]
    public async Task Handle_WhenAiBookingIsDisabled_ReturnsDisabledAndNeverCallsTheInterpreter()
    {
        _options = new AiBookingOptions { Enabled = false };
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("Book a meeting room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Disabled, result.Outcome);
        Assert.Null(result.Candidate);
        Assert.False(_interpreter.WasCalled);
    }

    [Fact]
    public async Task Handle_WhenTheInterpreterThrows_ReturnsProviderUnavailable()
    {
        _interpreter.NextException = new BookingIntentInterpretationException("The AI provider returned HTTP 500.");
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("Book a meeting room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Candidate);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Handle_WhenTheInterpreterNeverReturnsWithinTheConfiguredTimeout_ReturnsProviderUnavailable()
    {
        _options = new AiBookingOptions { Enabled = true, ApiKey = "test-key", RequestTimeoutSeconds = 0 };
        var hangingInterpreter = new HangingBookingIntentInterpreter();
        var sut = new ProposeBookingCommandHandler(
            hangingInterpreter, Options.Create(_options), _tenantRepository.Object, _resourceTypeRepository.Object,
            _resourceRepository.Object, _availabilityRuleRepository.Object, _blackoutPeriodRepository.Object,
            _bookingAvailabilityRepository.Object, _currentUserContext.Object, _timeProvider, NullLogger<ProposeBookingCommandHandler>.Instance);

        var result = await sut.Handle(new ProposeBookingCommandRequest("Book a meeting room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.ProviderUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Handle_ForTheGlobalSysAdmin_ThrowsTenantMembershipRequiredConflict()
    {
        _currentUserContext.SetupGet(c => c.TenantId).Returns((Guid?)null);
        var sut = CreateSut();

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => sut.Handle(new ProposeBookingCommandRequest("Book a meeting room tomorrow"), CancellationToken.None));

        Assert.Equal("Common.TenantMembershipRequired", exception.ErrorCode);
        Assert.False(_interpreter.WasCalled);
    }

    [Fact]
    public async Task Handle_WhenTheModelExplicitlyAsksForClarification_ReturnsItsQuestionVerbatim()
    {
        _interpreter.NextResponse = new LlmBookingIntent("en", "needs_clarification", "What date would you like?", null, null, null, null, null, null, null, null, null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("I need a room"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
        Assert.Equal("What date would you like?", result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-date")]
    [InlineData("2026-13-40")]
    public async Task Handle_WithAMissingOrMalformedDate_ReturnsNeedsClarification(string? date)
    {
        // `with` rather than ValidIntent(date: date): ValidIntent's own `date` parameter substitutes
        // TargetDate whenever the argument is null (so every OTHER test that doesn't care about the date
        // gets a valid one for free) - that substitution must be bypassed, not relied on, for the one test
        // case that specifically means "no date at all".
        _interpreter.NextResponse = ValidIntent() with { Date = date };
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    [Fact]
    public async Task Handle_WithADateInThePast_ReturnsNeedsClarification()
    {
        var pastDate = DateOnly.FromDateTime(ReferenceNowUtc.AddDays(-1).Date);
        _interpreter.NextResponse = ValidIntent(date: pastDate.ToString("yyyy-MM-dd"), dateIsRelative: false);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room yesterday"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    [Fact]
    public async Task Handle_WithNoStartTime_ReturnsNeedsClarification()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek); // open all day - the new day-wide check must NOT short-circuit here
        _interpreter.NextResponse = ValidIntent(startTime: null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    // The new feature this test file's sibling test (Handle_WithNoStartTimeAndTheResourceIsClosedThat...)
    // proves the positive case for: when the resource IS open at some point that day, asking for a start
    // time is still the right thing to do - re-asking might well produce a workable time.
    [Fact]
    public async Task Handle_WithNoStartTimeButTheResourceIsOpenThatDay_StillAsksForAStartTime()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(startTime: null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
        Assert.Contains("time", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The actual new behavior: the resource has NO AvailabilityRule at all covering TargetDate's day of
    // week - every time the user could possibly give would fail BookingEligibilityChecker's own
    // OutsideAvailability check anyway, so this is resolved as NoSuitableResource immediately, WITHOUT
    // ever asking "what time would you like to start" - proven by asserting the interpreter's own
    // clarification-question field is never even reachable here (the handler never calls back into the
    // model; this is a single Handle() call) and by the message itself.
    [Fact]
    public async Task Handle_WithNoStartTimeAndTheResourceIsClosedThatWholeDay_ReturnsNoSuitableResourceWithoutAskingForATime()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource]); // no AvailabilityRule configured for ANY day
        _interpreter.NextResponse = ValidIntent(startTime: null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
        Assert.Null(result.Candidate);
        Assert.DoesNotContain("what time", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Same short-circuit, but proving it is genuinely DAY-SPECIFIC, not "any rule missing at all": the
    // resource is open on a DIFFERENT day of the week than TargetDate, not closed outright.
    [Fact]
    public async Task Handle_WithNoStartTimeAndTheResourceIsOnlyOpenOnADifferentDayOfWeek_ReturnsNoSuitableResource()
    {
        var resource = CreateResource();
        var differentDay = (DayOfWeek)(((int)TargetDate.DayOfWeek + 1) % 7);
        SetupResourcesAndAvailability([resource], resource, differentDay);
        _interpreter.NextResponse = ValidIntent(startTime: null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
    }

    // The exact real-world bug this guards against: a multi-round clarification conversation can easily
    // end up with a start time ALREADY filled in (asked about, and answered, in an earlier round) by the
    // time the resource itself finally resolves to exactly one match. The day-wide check must still fire
    // here - checked UNCONDITIONALLY, not only when StartTime happens to still be null - otherwise the
    // request would sail past it, through the full end-time/eligibility checks, and only discover the
    // closed day at the very end, after already having asked for (and received) a time for nothing.
    [Fact]
    public async Task Handle_WhenAStartTimeWasAlreadyGivenButTheResourceIsClosedThatWholeDay_StillReturnsNoSuitableResourceWithoutNeedingTheFullEligibilityCheck()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource]); // no AvailabilityRule configured for ANY day
        _interpreter.NextResponse = ValidIntent(); // full, well-formed intent - start AND end time both given
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a meeting room tomorrow from 2 to 4 PM"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task Handle_WithNoEndTimeAndNoDuration_ReturnsNeedsClarification()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(endTime: null, durationMinutes: null);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow at 2pm"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    [Fact]
    public async Task Handle_WithADurationInsteadOfAnEndTime_ComputesTheEndTimeAndProposes()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(endTime: null, durationMinutes: 90);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room tomorrow at 2pm for 90 minutes"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(90), result.Candidate!.EndUtc - result.Candidate.StartUtc);
    }

    [Fact]
    public async Task Handle_WithEndTimeBeforeStartTime_ReturnsNeedsClarification()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(startTime: "16:00", endTime: "14:00");
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenTheResourceTypeHintMatchesNoRealType_ReturnsNoSuitableResource()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(resourceTypeHint: "spaceship launch pad");
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a spaceship launch pad"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
        Assert.Null(result.Candidate);
    }

    // The nearest unit-level equivalent of "AI cannot access another tenant's resources": this tenant's
    // OWN resource list (as the real tenant-filtered IResourceRepository would return it) simply never
    // contains another tenant's resource, so a name hint that happens to match one elsewhere can only ever
    // resolve to nothing - never to a cross-tenant row. The full-stack version of this, against the real
    // database and two real seeded tenants, lives in AiBookingsEndpointsTests.
    [Fact]
    public async Task Handle_WhenTheResourceNameHintMatchesNothingInThisTenantsOwnCatalog_ReturnsNoSuitableResource()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(resourceNameHint: "Globex Only Room");
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book the Globex Only Room"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenPartySizeExceedsEveryCandidatesCapacity_ReturnsNoSuitableResource()
    {
        var resource = CreateResource(capacity: 4);
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(partySize: 100);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room for 100 people"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenMultipleResourcesMatchEqually_ReturnsNeedsClarificationNamingBoth()
    {
        var resourceA = CreateResource(id: Guid.NewGuid());
        resourceA.Name = "Room A";
        var resourceB = CreateResource(id: Guid.NewGuid());
        resourceB.Name = "Room B";
        SetupResourcesAndAvailability([resourceA, resourceB], resourceA, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent();
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a meeting room"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
        Assert.Contains("Room A", result.Message);
        Assert.Contains("Room B", result.Message);
    }

    [Fact]
    public async Task Handle_WithAnExplicitQuantity_UsesItDirectlyRatherThanDerivingItFromPartySize()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(partySize: 5, quantity: 2);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book 2 units"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
        Assert.Equal(2, result.Candidate!.Quantity);
    }

    // No AvailabilityRule at all is configured for this resource/day - BookingEligibilityChecker (the
    // SAME function CreateBookingCommandHandler itself calls) reports OutsideAvailability, proving the
    // proposal flow is rejected by the real booking engine's own rules, not a separate copy of them.
    [Fact]
    public async Task Handle_WhenTheMatchedResourceIsNotActuallyEligibleForTheRequestedWindow_ReturnsNoSuitableResource()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource]); // no open day configured at all
        _interpreter.NextResponse = ValidIntent();
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a meeting room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, result.Outcome);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task Handle_WhenTheModelDeclaresBosnian_PropagatesBosnianAsTheResponseLanguage()
    {
        var resource = CreateResource();
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(language: "bs");
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("Treba mi sala za sastanke sutra od 14 do 16."), CancellationToken.None);

        Assert.Equal("bs", result.Language);
        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenTheModelDeclaresAnUnsupportedLanguage_FallsBackToHeuristicDetection()
    {
        _interpreter.NextResponse = ValidIntent(language: "fr") with { Date = null };
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("Treba mi sala sutra"), CancellationToken.None);

        Assert.Equal("bs", result.Language); // heuristic detects the Bosnian request text itself
    }

    [Fact]
    public async Task Handle_WhenTheMatchedResourcesZoneDiffersAndTheDateWasARelativeExpression_ReturnsNeedsClarification()
    {
        var resource = CreateResource(timeZoneId: "America/New_York");
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(dateIsRelative: true);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a meeting room tomorrow"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.NeedsClarification, result.Outcome);
    }

    [Fact]
    public async Task Handle_WhenTheMatchedResourcesZoneDiffersButTheDateWasAnExplicitDate_StillProposes()
    {
        var resource = CreateResource(timeZoneId: "America/New_York");
        SetupResourcesAndAvailability([resource], resource, TargetDate.DayOfWeek);
        _interpreter.NextResponse = ValidIntent(dateIsRelative: false);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a meeting room on that specific date"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
    }

    // Deterministic DST coverage (#7 in the task's acceptance list) - proves the handler routes the
    // resolved local date/time through AvailabilityCalculator.ConvertLocalToUtc (the SAME function every
    // other availability/booking path already uses - see docs/availability-and-timezones.md), inheriting
    // its existing, already-proven spring-forward policy, rather than a second, divergent conversion.
    // Europe/Sarajevo's transition date is computed dynamically (not hardcoded), matching this codebase's
    // own existing DST-test convention.
    [Fact]
    public async Task Handle_WithALocalTimeInsideASpringForwardGap_NormalizesForwardInsteadOfThrowing()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sarajevo");
        var year = ReferenceNowUtc.Year + 1;
        var springForwardDate = LastSundayOfMonth(year, 3);
        _tenantRepository.Setup(r => r.FindByIdAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Tenant { Id = TenantId, Name = "Acme", DefaultTimeZoneId = "Europe/Sarajevo", CreatedAtUtc = ReferenceNowUtc });
        var resource = CreateResource(timeZoneId: "Europe/Sarajevo");
        SetupResourcesAndAvailability([resource], resource, springForwardDate.DayOfWeek);
        // 02:30 local never occurs on the spring-forward day in Europe/Sarajevo (clocks jump 02:00 -> 03:00)
        // - AvailabilityCalculator.ConvertLocalToUtc's existing policy normalizes it forward to 03:30. The
        // end time is deliberately a further hour out (04:30, not 03:30) so that normalization can never
        // accidentally collide start and end at the identical instant and fail the (correct, separate)
        // ordering check instead of exercising what this test is actually about.
        _interpreter.NextResponse = ValidIntent(date: springForwardDate.ToString("yyyy-MM-dd"), startTime: "02:30", endTime: "04:30", dateIsRelative: false);
        var sut = CreateSut();

        var result = await sut.Handle(new ProposeBookingCommandRequest("book a room during the spring-forward gap"), CancellationToken.None);

        Assert.Equal(ProposeBookingOutcome.Proposed, result.Outcome);
        Assert.True(timeZone.IsInvalidTime(new DateTime(springForwardDate.Year, springForwardDate.Month, springForwardDate.Day, 2, 30, 0)));
    }

    private static DateOnly LastSundayOfMonth(int year, int month)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)lastDay.DayOfWeek - (int)DayOfWeek.Sunday + 7) % 7;
        return lastDay.AddDays(-offset);
    }

    private sealed class HangingBookingIntentInterpreter : IBookingIntentInterpreter
    {
        public async Task<LlmBookingIntent> InterpretAsync(
            string text, IReadOnlyList<BookingIntentTurn> priorTurns, BookingIntentContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable - Task.Delay(Infinite) always throws OperationCanceledException first.");
        }
    }
}
