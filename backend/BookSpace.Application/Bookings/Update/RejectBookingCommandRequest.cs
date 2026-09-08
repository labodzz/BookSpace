using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
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

// Unlike Approve, Reject never needs IResourceBookingLock: moving a booking OUT of Pending can only
// reduce active demand, which can never violate the capacity invariant - the same reasoning
// CancelBookingCommandHandler already relies on (see docs/bookings-and-concurrency.md).
public sealed class RejectBookingCommandHandler(
    IBookingRepository bookingRepository,
    IApprovalRequestRepository approvalRequestRepository,
    IResourceApproverRepository resourceApproverRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<RejectBookingCommandRequest, RejectBookingResponse>
{
    public async Task<RejectBookingResponse> Handle(RejectBookingCommandRequest request, CancellationToken cancellationToken)
    {
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        await ApprovalAuthorization.EnsureCallerCanDecideAsync(booking.ResourceId, resourceApproverRepository, currentUserContext, cancellationToken);

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

        await bookingRepository.SaveChangesAsync(cancellationToken);

        return new RejectBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status);
    }
}
