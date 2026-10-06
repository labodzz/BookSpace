using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

public sealed record CheckInBookingCommandRequest(Guid Id) : IRequest<CheckInBookingResponse>;

// AlreadyCheckedIn tells the client this call did NOT just perform a new check-in - it confirmed an
// existing one (CheckedInAtUtc is always the ORIGINAL instant in that case, never re-stamped to now). No
// validator needed: Id is a route-bound Guid with nothing else to validate (same as ApproveBookingRequest's
// sibling actions where the only input is the route id).
public sealed record CheckInBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    DateTimeOffset CheckedInAtUtc, bool AlreadyCheckedIn);

// Check-in never changes BookingStatus (it stays Confirmed) and never affects capacity, so by the same
// reasoning CancelBookingCommandHandler documents for itself, it does not strictly NEED IResourceBookingLock
// for the capacity invariant. It uses the lock anyway, for the same reason RejectBookingCommandHandler does:
// Booking has no RowVersion column (docs/optimistic-concurrency.md), so without a shared serialization
// point, two concurrent check-in requests for the SAME booking could both read CheckedInAtUtc == null and
// both write it - the second write silently overwriting the first with a different instant, which directly
// violates the idempotency requirement (a repeated check-in must never change the original CheckedInAtUtc).
// Sharing the resource-scoped lock closes this exactly like it closes Approve-vs-Reject: whichever request's
// RunExclusiveAsync acquisition wins commits first, and the second request's fresh re-read (inside its OWN
// lock acquisition, which cannot even begin until the first's transaction fully commits) sees
// CheckedInAtUtc already set and returns AlreadyCheckedIn instead of overwriting it. The same lock also
// protects a future no-show job's transition away from Confirmed, PROVIDED that job acquires the identical
// IResourceBookingLock before mutating Booking.Status - exactly like StaleApprovalProcessor already does
// against Approve/Reject (see docs/background-jobs.md, "Stale-approval job - resource lock").
//
// CancelBookingCommandHandler now shares this SAME lock too (not for its own capacity invariant, which
// never needed one - but to coordinate with THIS handler and the future no-show job). A check-in racing
// with a cancel on the same booking can therefore no longer leave CheckedInAtUtc set on a Cancelled
// booking, or a Cancel silently discard a just-recorded check-in: whichever wins the lock commits first,
// the loser's fresh re-read sees the authoritative result (check-in sees Status != Confirmed and refuses;
// cancel sees CheckedInAtUtc already set and refuses with Booking.AlreadyCheckedIn) - see
// docs/bookings-and-concurrency.md, "Booking check-in - the check-in/cancel race" for the full before/
// after analysis and the real-LocalDB proof (CheckInVsCancelConcurrencyTests).
public sealed class CheckInBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IBookingRepository bookingRepository,
    ITenantRepository tenantRepository,
    IResourceApproverRepository resourceApproverRepository,
    ICurrentUserContext currentUserContext)
    : IRequestHandler<CheckInBookingCommandRequest, CheckInBookingResponse>
{
    public async Task<CheckInBookingResponse> Handle(CheckInBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // A projection, not a tracked load - see ApproveBookingCommandHandler/IBookingRepository for why:
        // the fresh re-read below (the first tracked load, taken after the lock is held) would otherwise
        // silently hit this same DbContext's identity map and return stale data.
        var resourceId = await bookingRepository.FindResourceIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        return await resourceBookingLock.RunExclusiveAsync(resourceId, ct => CheckInUnderLockAsync(request, resourceId, ct), cancellationToken);
    }

    private async Task<CheckInBookingResponse> CheckInUnderLockAsync(CheckInBookingCommandRequest request, Guid resourceId, CancellationToken cancellationToken)
    {
        // The first (and only) tracked load of this Booking on this DbContext - genuinely fresh, taken
        // after the lock is held. Never trust the pre-lock projection above for anything but the resource id.
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        if (!await CanCheckInAsync(booking, resourceId, cancellationToken))
        {
            // Not-owner, not-admin, and not-an-approver-for-this-resource all look identical to the caller -
            // same philosophy as CancelBookingCommandHandler: never confirm another user's booking exists
            // via a different error shape. Unlike ApprovalAuthorization's own forbidden check (a 409, since
            // an Approver who already sees a booking in their pending-approval queue "already knows" it
            // exists), check-in has no such queue naming a specific booking to a would-be approver, so there
            // is nothing to be more honest about here - a plain 404 is the right, narrower default.
            throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);
        }

        var tenant = await tenantRepository.FindByIdAsync(booking.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {booking.TenantId} for booking {booking.Id} was not found.");

        // Captured once and reused for both the eligibility evaluation and the actual CheckedInAtUtc write,
        // so the two can never disagree about "now".
        var nowUtc = DateTimeOffset.UtcNow;

        switch (CheckInEligibilityChecker.Evaluate(booking, tenant, nowUtc))
        {
            case CheckInEligibility.AlreadyCheckedIn:
                // Idempotent: the ORIGINAL CheckedInAtUtc is returned unchanged, no mutation, no save, no
                // re-validation of the window - confirming an existing check-in can never fail just because
                // the window has since closed.
                return ToResponse(booking, booking.CheckedInAtUtc!.Value, alreadyCheckedIn: true);

            case CheckInEligibility.NotConfirmed:
                throw new ConflictException(
                    $"Booking {booking.Id} cannot be checked in from status {booking.Status}.", ErrorCodes.BookingCheckInNotAllowed);

            case CheckInEligibility.TooEarly:
                throw new ConflictException(
                    $"Booking {booking.Id} cannot be checked in yet - the check-in window has not opened.", ErrorCodes.BookingCheckInNotAllowed);

            case CheckInEligibility.TooLatePastGracePeriod:
                throw new ConflictException(
                    $"Booking {booking.Id} can no longer be checked in - the check-in window has closed.", ErrorCodes.BookingCheckInNotAllowed);
        }

        booking.CheckedInAtUtc = nowUtc;
        await bookingRepository.SaveChangesAsync(cancellationToken);
        return ToResponse(booking, nowUtc, alreadyCheckedIn: false);
    }

    // Member: only their own booking. ResourceApprover: any booking on a resource they are actually
    // assigned to (ResourceApprover is a resource-level grant, independent of whether that specific
    // booking ever needed approval - see IResourceApproverRepository). TenantAdmin: anything in their
    // own tenant, enforced automatically by the global query filter already scoping FindByIdAsync.
    // SysAdmin is deliberately excluded - see ApprovalAuthorization's own doc comment.
    private async Task<bool> CanCheckInAsync(Booking booking, Guid resourceId, CancellationToken cancellationToken)
    {
        if (booking.UserId == currentUserContext.UserId || ApprovalAuthorization.IsTenantAdmin(currentUserContext))
        {
            return true;
        }

        return currentUserContext.UserId is { } userId
            && await resourceApproverRepository.FindByResourceAndUserAsync(resourceId, userId, cancellationToken) is not null;
    }

    private static CheckInBookingResponse ToResponse(Booking booking, DateTimeOffset checkedInAtUtc, bool alreadyCheckedIn) =>
        new(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status, checkedInAtUtc, alreadyCheckedIn);
}
