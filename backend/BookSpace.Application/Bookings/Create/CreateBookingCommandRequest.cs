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

// TimeZoneId is the resource's own IANA zone, included so the client can show the new booking's local
// time at the resource without a follow-up resource lookup.
public sealed record CreateBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status, string TimeZoneId);

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
// Eligibility (status/availability/blackout/capacity) is delegated to BookingEligibilityChecker, shared
// with CreateRecurringSeriesCommandHandler and ApproveBookingCommandHandler - see
// docs/recurring-bookings-and-approvals.md.
public sealed class CreateBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    IBookingRepository bookingRepository,
    IResourceApproverRepository resourceApproverRepository,
    IApprovalRequestRepository approvalRequestRepository,
    ITenantRepository tenantRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateBookingCommandRequest, CreateBookingResponse>
{
    public Task<CreateBookingResponse> Handle(CreateBookingCommandRequest request, CancellationToken cancellationToken) =>
        resourceBookingLock.RunExclusiveAsync(request.ResourceId, ct => CreateUnderLockAsync(request, ct), cancellationToken);

    private async Task<CreateBookingResponse> CreateUnderLockAsync(CreateBookingCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var blackouts = await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var eligibility = await BookingEligibilityChecker.CheckAsync(
            resource, request.StartUtc, request.EndUtc, request.Quantity, excludeBookingId: null,
            rules, blackouts, bookingAvailabilityRepository, cancellationToken);
        BookingEligibilityChecker.ThrowIfNotEligible(eligibility, resource.Id);

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

        // A Resource with RequiresApproval never goes straight to Confirmed - it needs at least one
        // ResourceApprover configured (rejected outright otherwise, rather than silently confirming a
        // booking that was supposed to require approval, or creating a Pending booking nobody could ever
        // approve) - see docs/recurring-bookings-and-approvals.md.
        if (resource.RequiresApproval)
        {
            var approvers = await resourceApproverRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
            if (approvers.Count == 0)
            {
                throw new ConflictException(
                    $"Resource {resource.Id} requires approval but has no approvers configured.", ErrorCodes.BookingNoApproverConfigured);
            }

            var tenant = await tenantRepository.FindByIdAsync(booking.TenantId, cancellationToken)
                ?? throw new InvalidOperationException($"Tenant {booking.TenantId} for the current request was not found.");
            booking.Status = BookingStatus.Pending;

            await bookingRepository.AddAsync(booking, cancellationToken);
            await approvalRequestRepository.AddAsync(
                new ApprovalRequest
                {
                    Id = Guid.NewGuid(),
                    TenantId = booking.TenantId,
                    BookingId = booking.Id,
                    ApproverId = null,
                    Status = ApprovalStatus.Pending,
                    RequestedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(tenant.ApprovalExpiryHours),
                },
                cancellationToken);
        }
        else
        {
            await bookingRepository.AddAsync(booking, cancellationToken);
        }

        await bookingRepository.SaveChangesAsync(cancellationToken);

        return new CreateBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status, resource.TimeZoneId);
    }
}
