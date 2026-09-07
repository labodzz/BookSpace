using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

public sealed record CreateRecurringSeriesCommandRequest(
    Guid ResourceId,
    DateOnly StartDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly? EndDate,
    int? OccurrenceCount,
    int Quantity = 1) : IRequest<CreateRecurringSeriesResponse>;

// RequestedOccurrenceCount is the number of candidate dates the recurrence pattern produced;
// CreatedOccurrences + Conflicts.Count always equals it - no candidate date is ever silently dropped.
public sealed record CreateRecurringSeriesResponse(
    Guid SeriesId,
    Guid ResourceId,
    int RequestedOccurrenceCount,
    IReadOnlyList<CreateRecurringSeriesOccurrenceResponse> CreatedOccurrences,
    IReadOnlyList<CreateRecurringSeriesConflictResponse> Conflicts);

public sealed record CreateRecurringSeriesOccurrenceResponse(Guid Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, BookingStatus Status);

// Reason is a stable ErrorCode string (Booking.OutsideAvailability, Booking.BlackoutConflict,
// Booking.CapacityExceeded, Booking.ResourceUnavailable, or NonexistentLocalTime for a spring-forward
// gap) - the same machine-readable contract as a single booking-creation rejection, per occurrence.
public sealed record CreateRecurringSeriesConflictResponse(DateOnly Date, string Reason);

public sealed class CreateRecurringSeriesCommandRequestValidator : AbstractValidator<CreateRecurringSeriesCommandRequest>
{
    public CreateRecurringSeriesCommandRequestValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.StartDate)
            .Must(startDate => startDate >= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Series cannot start in the past.");
        RuleFor(command => command.EndTime).GreaterThan(command => command.StartTime);
        RuleFor(command => command.Frequency)
            .Must(frequency => frequency is RecurrenceFrequency.Daily or RecurrenceFrequency.Weekly or RecurrenceFrequency.Monthly)
            .WithMessage("Only Daily, Weekly, and Monthly recurrence are supported.");
        RuleFor(command => command.Interval).GreaterThan(0);
        RuleFor(command => command.Quantity).GreaterThan(0);
        RuleFor(command => command)
            .Must(command => command.EndDate.HasValue ^ command.OccurrenceCount.HasValue)
            .WithMessage("Specify exactly one of EndDate or OccurrenceCount.")
            .WithName(nameof(CreateRecurringSeriesCommandRequest.EndDate));
        RuleFor(command => command.EndDate)
            .GreaterThanOrEqualTo(command => command.StartDate)
            .When(command => command.EndDate.HasValue);
        // Bounds total generated occurrences before any DB work happens - RecurringOccurrenceGenerator
        // itself hard-stops at the same MaxOccurrences as a backstop, but rejecting a pathological
        // request here (e.g. a 50-year daily series) is cheaper and gives a clearer client-facing error
        // than silently truncating it after the fact.
        RuleFor(command => command)
            .Must(command => EstimateOccurrenceCount(command) <= RecurringOccurrenceGenerator.MaxOccurrences)
            .WithMessage($"This recurrence would generate more than {RecurringOccurrenceGenerator.MaxOccurrences} occurrences.")
            .WithName(nameof(CreateRecurringSeriesCommandRequest.EndDate));
    }

    private static int EstimateOccurrenceCount(CreateRecurringSeriesCommandRequest command)
    {
        if (command.OccurrenceCount is { } count)
        {
            return count;
        }

        if (command.EndDate is not { } endDate || endDate < command.StartDate)
        {
            return 0;
        }

        var totalDays = endDate.DayNumber - command.StartDate.DayNumber;
        var interval = Math.Max(command.Interval, 1);
        return command.Frequency switch
        {
            RecurrenceFrequency.Daily => (totalDays / interval) + 1,
            RecurrenceFrequency.Weekly => (totalDays / (7 * interval)) + 1,
            // Conservative (shortest-month) estimate, so this never UNDER-counts a real monthly series.
            RecurrenceFrequency.Monthly => (totalDays / (28 * interval)) + 1,
            _ => int.MaxValue,
        };
    }
}

// Every candidate occurrence is evaluated - none are ever silently skipped. A candidate date either
// becomes a materialized Booking (Confirmed, or Pending if the resource RequiresApproval - exactly the
// same rule CreateBookingCommandHandler applies to a single booking) or is reported back as a Conflict
// with its exact reason. The whole operation runs inside ONE IResourceBookingLock acquisition (see
// docs/recurring-bookings-and-approvals.md): occurrences within a single series never overlap each other
// in time by construction (each is on a distinct calendar date, EndTime > StartTime same-day, validated),
// so accumulated-but-not-yet-saved occurrences never need to be counted against each other's capacity
// check - only already-committed rows (from other bookings/series) matter, and those are re-read fresh
// per occurrence via IBookingAvailabilityRepository.
public sealed class CreateRecurringSeriesCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    IBookingRepository bookingRepository,
    IRecurringSeriesRepository recurringSeriesRepository,
    IResourceApproverRepository resourceApproverRepository,
    IApprovalRequestRepository approvalRequestRepository,
    ITenantRepository tenantRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateRecurringSeriesCommandRequest, CreateRecurringSeriesResponse>
{
    public Task<CreateRecurringSeriesResponse> Handle(CreateRecurringSeriesCommandRequest request, CancellationToken cancellationToken) =>
        resourceBookingLock.RunExclusiveAsync(request.ResourceId, ct => CreateUnderLockAsync(request, ct), cancellationToken);

    private async Task<CreateRecurringSeriesResponse> CreateUnderLockAsync(CreateRecurringSeriesCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        Tenant? tenant = null;
        if (resource.RequiresApproval)
        {
            var approvers = await resourceApproverRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
            if (approvers.Count == 0)
            {
                throw new ConflictException(
                    $"Resource {resource.Id} requires approval but has no approvers configured.", ErrorCodes.BookingNoApproverConfigured);
            }

            tenant = await tenantRepository.FindByIdAsync(currentUserContext.TenantId!.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Tenant {currentUserContext.TenantId} for the current request was not found.");
        }

        var series = new RecurringSeries
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceId = resource.Id,
            UserId = currentUserContext.UserId!.Value,
            StartDate = request.StartDate,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            TimeZoneId = resource.TimeZoneId,
            Frequency = request.Frequency,
            Interval = request.Interval,
            EndDate = request.EndDate,
            OccurrenceCount = request.OccurrenceCount,
            Quantity = request.Quantity,
        };

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(resource.TimeZoneId);
        var candidateDates = RecurringOccurrenceGenerator.GenerateCandidateDates(series);

        // Fetched once, reused across every candidate occurrence - see BookingEligibilityChecker's
        // own comment for why (avoids hundreds of redundant identical round-trips for a long series).
        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var blackouts = await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken);

        var bookings = new List<Booking>();
        var approvalRequests = new List<ApprovalRequest>();
        var conflicts = new List<CreateRecurringSeriesConflictResponse>();

        foreach (var date in candidateDates)
        {
            var resolution = RecurringOccurrenceGenerator.TryResolveOccurrence(date, request.StartTime, request.EndTime, timeZone);
            if (!resolution.Succeeded)
            {
                conflicts.Add(new CreateRecurringSeriesConflictResponse(date, resolution.ConflictReason!));
                continue;
            }

            var startUtc = resolution.StartUtc!.Value;
            var endUtc = resolution.EndUtc!.Value;

            var eligibility = await BookingEligibilityChecker.CheckAsync(
                resource, startUtc, endUtc, request.Quantity, excludeBookingId: null,
                rules, blackouts, bookingAvailabilityRepository, cancellationToken);

            if (eligibility != BookingEligibility.Eligible)
            {
                conflicts.Add(new CreateRecurringSeriesConflictResponse(date, BookingEligibilityChecker.ToErrorCode(eligibility)));
                continue;
            }

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                TenantId = series.TenantId,
                ResourceId = resource.Id,
                UserId = series.UserId,
                SeriesId = series.Id,
                StartUtc = startUtc,
                EndUtc = endUtc,
                Quantity = request.Quantity,
                Status = resource.RequiresApproval ? BookingStatus.Pending : BookingStatus.Confirmed,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            bookings.Add(booking);

            if (resource.RequiresApproval)
            {
                approvalRequests.Add(new ApprovalRequest
                {
                    Id = Guid.NewGuid(),
                    TenantId = series.TenantId,
                    BookingId = booking.Id,
                    ApproverId = null,
                    Status = ApprovalStatus.Pending,
                    RequestedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(tenant!.ApprovalExpiryHours),
                });
            }
        }

        if (bookings.Count == 0)
        {
            throw new ConflictException(
                $"No occurrence in the requested recurrence for resource {resource.Id} could be created.", ErrorCodes.RecurringSeriesNoValidOccurrences);
        }

        await recurringSeriesRepository.AddAsync(series, cancellationToken);
        foreach (var booking in bookings)
        {
            await bookingRepository.AddAsync(booking, cancellationToken);
        }

        foreach (var approvalRequest in approvalRequests)
        {
            await approvalRequestRepository.AddAsync(approvalRequest, cancellationToken);
        }

        await recurringSeriesRepository.SaveChangesAsync(cancellationToken);

        return new CreateRecurringSeriesResponse(
            series.Id,
            resource.Id,
            candidateDates.Count,
            bookings.Select(booking => new CreateRecurringSeriesOccurrenceResponse(booking.Id, booking.StartUtc, booking.EndUtc, booking.Status)).ToList(),
            conflicts);
    }
}
