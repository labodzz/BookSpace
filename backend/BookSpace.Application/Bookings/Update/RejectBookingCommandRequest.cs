using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

public sealed record RejectBookingCommandRequest(Guid Id, string? DecisionNote) : IRequest<RejectBookingResponse>;

public sealed record RejectBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

public sealed class RejectBookingCommandRequestValidator : AbstractValidator<RejectBookingCommandRequest>
{
    public RejectBookingCommandRequestValidator()
    {
        RuleFor(command => command.DecisionNote).MaximumLength(2000);
    }
}

// Reject needs no eligibility re-check the way Approve does: moving a booking OUT of Pending can only
// reduce active demand, which can never violate the capacity invariant (see docs/bookings-and-
// concurrency.md). It DOES still need IResourceBookingLock, though, for a different reason: Approve and
// Reject are two different decisions that can race for the SAME booking, and Booking deliberately has no
// RowVersion column (see docs/optimistic-concurrency.md - justified by Cancel's idempotency, which never
// considered this case). Without a shared serialization point, two concurrent decisions on one booking -
// e.g. two different ResourceApprovers, one approving and one rejecting at the same instant - could both
// "succeed" with no exception raised to either caller, leaving Booking.Status and ApprovalRequest.Status
// silently inconsistent depending on which SaveChanges happened to commit last. Sharing the resource-
// scoped lock with Approve closes this the same way it already closes Approve-vs-Approve and
// Approve-vs-Create/UpdateResource races - proven by
// BookSpace.Infrastructure.Tests.ApprovalConcurrencyTests.ConcurrentApproveAndRejectOnTheSameBooking_ExactlyOneDecisionWinsCleanly.
public sealed class RejectBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IBookingRepository bookingRepository,
    IApprovalRequestRepository approvalRequestRepository,
    IResourceApproverRepository resourceApproverRepository,
    ICurrentUserContext currentUserContext,
    INotificationOutboxWriter notificationOutboxWriter)
    : IRequestHandler<RejectBookingCommandRequest, RejectBookingResponse>
{
    public async Task<RejectBookingResponse> Handle(RejectBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // A projection, not a tracked load - see ApproveBookingCommandHandler (and docs/recurring-
        // bookings-and-approvals.md §8) for why: the later genuinely-fresh read inside the lock must be
        // the first tracked load of this Booking on this DbContext, or EF Core's identity map would
        // silently return this same stale instance instead of refreshing it.
        var resourceId = await bookingRepository.FindResourceIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        await ApprovalAuthorization.EnsureCallerCanDecideAsync(resourceId, resourceApproverRepository, currentUserContext, cancellationToken);

        return await resourceBookingLock.RunExclusiveAsync(resourceId, ct => RejectUnderLockAsync(request, ct), cancellationToken);
    }

    private async Task<RejectBookingResponse> RejectUnderLockAsync(RejectBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // The first (and only) tracked load of this Booking on this DbContext - genuinely fresh, taken
        // after the lock is held.
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        if (booking.Status != BookingStatus.Pending)
        {
            throw new ConflictException($"Booking {booking.Id} is not awaiting approval.", ErrorCodes.BookingApprovalNotAllowed);
        }

        var approvalRequest = await approvalRequestRepository.FindByBookingIdAsync(booking.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Booking {booking.Id} is Pending but has no ApprovalRequest.");

        booking.Status = BookingStatus.Rejected;
        approvalRequest.Status = ApprovalStatus.Rejected;
        approvalRequest.ApproverId = currentUserContext.UserId;
        approvalRequest.DecisionNote = request.DecisionNote;
        approvalRequest.DecidedAtUtc = DateTimeOffset.UtcNow;

        // EnqueueAsync calls SaveChangesAsync itself, committing the Booking/ApprovalRequest mutation and
        // the new NotificationOutboxItem row together - see CreateBookingCommandHandler for the same
        // pattern. No separate bookingRepository.SaveChangesAsync call: a failure here leaves neither
        // persisted.
        await notificationOutboxWriter.EnqueueAsync(BookingNotificationFactory.Rejection(booking), cancellationToken);

        return new RejectBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status);
    }
}
