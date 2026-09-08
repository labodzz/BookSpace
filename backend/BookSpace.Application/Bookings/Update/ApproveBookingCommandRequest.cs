using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

public sealed record ApproveBookingCommandRequest(Guid Id, string? DecisionNote) : IRequest<ApproveBookingResponse>;

public sealed record ApproveBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

public sealed class ApproveBookingCommandRequestValidator : AbstractValidator<ApproveBookingCommandRequest>
{
    public ApproveBookingCommandRequestValidator()
    {
        RuleFor(command => command.DecisionNote).MaximumLength(2000);
    }
}

// Availability MUST be re-checked at approval time, not trusted from creation time - see
// docs/recurring-bookings-and-approvals.md. Protected by the same IResourceBookingLock as booking
// creation and UpdateResourceCommandHandler's capacity-reduction check: the re-check and the
// Pending -> Confirmed write happen inside one locked transaction, so a concurrent create/approve/
// capacity-update for the same resource can never interleave with this decision. If eligibility no
// longer holds, this throws before any write - the booking stays exactly Pending, never partially
// approved.
public sealed class ApproveBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IBookingRepository bookingRepository,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    IApprovalRequestRepository approvalRequestRepository,
    IResourceApproverRepository resourceApproverRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<ApproveBookingCommandRequest, ApproveBookingResponse>
{
    public async Task<ApproveBookingResponse> Handle(ApproveBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // A projection, not a tracked load (see IBookingRepository.FindResourceIdAsync) - deliberately
        // so this pre-lock step never adds the Booking to this DbContext's change tracker. If it did,
        // the "fresh" re-read below (after the lock is acquired) would hit the SAME DbContext's identity
        // map and silently return that SAME stale tracked instance instead of the row's current state,
        // defeating the entire point of re-reading under the lock.
        var resourceId = await bookingRepository.FindResourceIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        await ApprovalAuthorization.EnsureCallerCanDecideAsync(resourceId, resourceApproverRepository, currentUserContext, cancellationToken);

        return await resourceBookingLock.RunExclusiveAsync(resourceId, ct => ApproveUnderLockAsync(request, resourceId, ct), cancellationToken);
    }

    private async Task<ApproveBookingResponse> ApproveUnderLockAsync(ApproveBookingCommandRequest request, Guid resourceId, CancellationToken cancellationToken)
    {
        // The first (and only) tracked load of this Booking on this DbContext - genuinely fresh, taken
        // after the lock is held.
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        if (booking.Status != BookingStatus.Pending)
        {
            throw new ConflictException($"Booking {booking.Id} is not awaiting approval.", ErrorCodes.BookingApprovalNotAllowed);
        }

        var resource = await resourceRepository.FindByIdAsync(resourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {resourceId} was not found.", ErrorCodes.ResourceNotFound);

        var rules = await availabilityRuleRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var blackouts = await blackoutPeriodRepository.GetByResourceIdAsync(resource.Id, cancellationToken);
        var eligibility = await BookingEligibilityChecker.CheckAsync(
            resource, booking.StartUtc, booking.EndUtc, booking.Quantity, excludeBookingId: booking.Id,
            rules, blackouts, bookingAvailabilityRepository, cancellationToken);
        BookingEligibilityChecker.ThrowIfNotEligible(eligibility, resource.Id);

        var approvalRequest = await approvalRequestRepository.FindByBookingIdAsync(booking.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Booking {booking.Id} is Pending but has no ApprovalRequest.");

        booking.Status = BookingStatus.Confirmed;
        approvalRequest.Status = ApprovalStatus.Approved;
        approvalRequest.ApproverId = currentUserContext.UserId;
        approvalRequest.DecisionNote = request.DecisionNote;
        approvalRequest.DecidedAtUtc = DateTimeOffset.UtcNow;

        await bookingRepository.SaveChangesAsync(cancellationToken);

        return new ApproveBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status);
    }
}
