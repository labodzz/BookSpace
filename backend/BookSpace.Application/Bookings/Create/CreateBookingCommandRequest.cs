using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

public sealed record CreateBookingCommandRequest(Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity = 1)
    : IRequest<CreateBookingResponse>;

public sealed record CreateBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

public sealed class CreateBookingCommandRequestValidator : AbstractValidator<CreateBookingCommandRequest>
{
    public CreateBookingCommandRequestValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.StartUtc)
            .Must(startUtc => startUtc >= DateTimeOffset.UtcNow)
            .WithMessage("Booking cannot start in the past.");
        RuleFor(command => command.EndUtc).GreaterThan(command => command.StartUtc);
        RuleFor(command => command.Quantity).GreaterThan(0);
    }
}

// The concurrency boundary lives in IResourceBookingLock.RunExclusiveAsync (see
// docs/bookings-and-concurrency.md): everything from the resource-status check through the insert runs
// inside one transaction, holding an exclusive lock on the resource row for its whole duration, so two
// concurrent requests for the same resource can never both pass this handler's own capacity re-check.
// Every check below re-derives its answer from a fresh read taken under that lock - it never trusts a
// value computed before the lock was acquired.
public sealed class CreateBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    IBookingRepository bookingRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateBookingCommandRequest, CreateBookingResponse>
{
    public Task<CreateBookingResponse> Handle(CreateBookingCommandRequest request, CancellationToken cancellationToken) =>
        resourceBookingLock.RunExclusiveAsync(request.ResourceId, ct => CreateUnderLockAsync(request, ct), cancellationToken);

    private async Task<CreateBookingResponse> CreateUnderLockAsync(CreateBookingCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        // Mirrors the availability query's own gating: a resource that isn't Active has no bookable
        // slots at all, regardless of what its schedule/blackouts/capacity would otherwise compute.
        if (resource.Status != ResourceStatus.Active)
        {
            throw new ConflictException($"Resource {resource.Id} is not available for booking.", ErrorCodes.BookingResourceUnavailable);
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(resource.TimeZoneId);
        var fromDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.StartUtc, timeZone).Date);
        var toDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.EndUtc, timeZone).Date);

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var openPeriods = AvailabilityCalculator.ComputeOpenPeriodsUtc(rules, fromDate, toDate, timeZone);

        var window = (request.StartUtc, request.EndUtc);
        if (!IntervalMath.Covers(window, openPeriods))
        {
            throw new ConflictException("The requested time is outside the resource's availability.", ErrorCodes.BookingOutsideAvailability);
        }

        var blackouts = await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var hasBlackoutConflict = blackouts.Any(period => period.StartUtc < request.EndUtc && period.EndUtc > request.StartUtc);
        if (hasBlackoutConflict)
        {
            throw new ConflictException("The requested time overlaps a blackout period.", ErrorCodes.BookingBlackoutConflict);
        }

        var overlappingBookings = await bookingAvailabilityRepository.GetActiveBookingsAsync(
            resource.Id, request.StartUtc, request.EndUtc, cancellationToken);
        var occupancies = overlappingBookings.Select(booking => (booking.StartUtc, booking.EndUtc, Amount: booking.Quantity)).ToList();
        var availableSlots = IntervalMath.ComputeAvailableCapacity(window, resource.Capacity, occupancies);
        var usableSlots = availableSlots
            .Where(slot => slot.AvailableCapacity >= request.Quantity)
            .Select(slot => (slot.Start, slot.End));

        if (!IntervalMath.Covers(window, usableSlots))
        {
            throw new ConflictException("The requested time does not have enough remaining capacity.", ErrorCodes.BookingCapacityExceeded);
        }

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceId = resource.Id,
            UserId = currentUserContext.UserId!.Value,
            StartUtc = request.StartUtc,
            EndUtc = request.EndUtc,
            Quantity = request.Quantity,
            Status = BookingStatus.Confirmed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await bookingRepository.AddAsync(booking, cancellationToken);
        await bookingRepository.SaveChangesAsync(cancellationToken);

        return new CreateBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status);
    }
}
