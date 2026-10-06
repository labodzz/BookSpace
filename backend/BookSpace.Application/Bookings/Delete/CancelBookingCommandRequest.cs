using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

// CancelRemainingSeries distinguishes cancelling ONLY this occurrence (default, false) from cancelling
// this occurrence AND every later still-cancellable occurrence in the same series (true) - see
// docs/recurring-bookings-and-approvals.md. Ignored (no error) for a booking with no SeriesId - a
// one-off booking has no "remaining series" to cascade into.
//
// Reason is optional and applies to a TenantAdmin cancelling someone ELSE's booking (see
// docs/open-questions.md, "TenantAdmin Cancellation of Another User's Booking") - it is stored as-is on
// CancellationReason so the affected owner can see why in their booking history, even though there is
// still no notification mechanism to actively tell them (deferred to a later work packet). Left optional
// rather than required: this is the interim, minimal half of that open question (audit trail now, active
// notification later), not the final product decision.
public sealed record CancelBookingCommandRequest(Guid Id, bool CancelRemainingSeries = false, string? Reason = null) : IRequest<CancelBookingResponse>;

// CascadedOccurrenceIds lists every OTHER occurrence also cancelled by this call (empty unless
// CancelRemainingSeries was true and the booking belongs to a series) - the client is always told exactly
// what else was affected, never left to infer it.
public sealed record CancelBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    IReadOnlyList<Guid> CascadedOccurrenceIds);

public sealed class CancelBookingCommandRequestValidator : AbstractValidator<CancelBookingCommandRequest>
{
    public CancelBookingCommandRequestValidator()
    {
        RuleFor(command => command.Reason).MaximumLength(1000);
    }
}

// Cancellation alone can only ever reduce summed demand, which can never turn a valid capacity state into
// an invalid one - that invariant still needs no lock. But Cancel is no longer the only operation that can
// mutate a Confirmed booking: CheckInBookingCommandHandler sets CheckedInAtUtc under IResourceBookingLock,
// and a future no-show job will transition Confirmed -> NoShow under the same lock (see
// docs/background-jobs.md). Without Cancel ALSO sharing that lock, a check-in and a cancel racing on the
// SAME booking could both read Confirmed before either commits and both "succeed" - a cancelled booking
// that also ends up bearing a CheckedInAtUtc value, or a checked-in booking silently cancelled out from
// under its own just-recorded attendance. Sharing the resource-scoped lock closes this exactly like it
// already closes Approve-vs-Reject and Check-in-vs-Check-in: whichever request's RunExclusiveAsync
// acquisition wins commits first, and the loser's fresh re-read (inside its OWN lock acquisition, which
// cannot even begin until the winner's transaction fully commits) sees the current, authoritative state -
// never a half-updated one. See docs/bookings-and-concurrency.md ("Booking check-in - the check-in/cancel
// race") for the full before/after analysis and the two race-order outcomes this closes.
public sealed class CancelBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IBookingRepository bookingRepository, ICurrentUserContext currentUserContext, INotificationOutboxWriter notificationOutboxWriter)
    : IRequestHandler<CancelBookingCommandRequest, CancelBookingResponse>
{
    private static readonly BookingStatus[] CancellableStatuses = [BookingStatus.Pending, BookingStatus.Confirmed];

    public async Task<CancelBookingResponse> Handle(CancelBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // A projection, not a tracked load - see ApproveBookingCommandHandler/IBookingRepository for why:
        // the fresh re-read below (the first tracked load, taken after the lock is held) would otherwise
        // silently hit this same DbContext's identity map and return stale data. Every occurrence of a
        // recurring series shares the SAME ResourceId as its series (set once at series-creation time and
        // never mutated afterward - see CreateRecurringSeriesCommandHandler), so locking the PRIMARY
        // booking's resource is sufficient for the whole CancelRemainingSeries cascade below; there is no
        // multi-resource lock-ordering concern to solve here, the same assumption
        // ApproveBookingCommandHandler's own ApproveRemainingSeries cascade already relies on.
        var resourceId = await bookingRepository.FindResourceIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);

        return await resourceBookingLock.RunExclusiveAsync(resourceId, ct => CancelUnderLockAsync(request, ct), cancellationToken);
    }

    private async Task<CancelBookingResponse> CancelUnderLockAsync(CancelBookingCommandRequest request, CancellationToken cancellationToken)
    {
        // The first (and only) tracked load of this Booking on this DbContext - genuinely fresh, taken
        // after the lock is held. Never trust the pre-lock projection above for anything but the resource id.
        var booking = await bookingRepository.FindByIdAsync(request.Id, cancellationToken);

        // Not-found, "belongs to a different user", and "a different user who isn't a TenantAdmin" all
        // look identical to the caller - never confirm another user's booking exists via a different
        // error shape (same philosophy already applied to cross-tenant lookups elsewhere:
        // NotFoundException, not a 403, so nothing about someone else's data is leaked by the response
        // shape). TenantAdmin bypasses the ownership check the same way it already bypasses the
        // per-resource approver check in ApprovalAuthorization. SysAdmin does not - see that type's own
        // doc comment.
        var isOwner = booking is not null && booking.UserId == currentUserContext.UserId;
        if (booking is null || !(isOwner || ApprovalAuthorization.IsTenantAdmin(currentUserContext)))
        {
            throw new NotFoundException($"Booking {request.Id} was not found.", ErrorCodes.BookingNotFound);
        }

        var cascadedIds = new List<Guid>();

        if (booking.Status != BookingStatus.Cancelled)
        {
            // A checked-in booking is never retroactively cancelled through this ordinary member/admin
            // cancel path - thrown BEFORE any mutation, so a booking that loses this check is left in
            // exactly the state it was already in (no partial write to roll back). This is NOT a
            // widened admin bypass: TenantAdmin/SysAdmin's bypass above only ever widens WHO may act (the
            // ownership check), never WHICH states are eligible to cancel - CancellableStatuses right below
            // already applies unconditionally to every caller including admins, and this rule follows that
            // identical, pre-existing precedent rather than inventing a new one. CheckedInAtUtc is never
            // cleared or overwritten by Cancel in any code path.
            if (booking.CheckedInAtUtc is not null)
            {
                throw new ConflictException(
                    $"Booking {booking.Id} has already been checked in and cannot be cancelled.", ErrorCodes.BookingAlreadyCheckedIn);
            }

            if (!CancellableStatuses.Contains(booking.Status))
            {
                throw new ConflictException(
                    $"Booking {booking.Id} cannot be cancelled from status {booking.Status}.", ErrorCodes.BookingCancellationNotAllowed);
            }

            CancelOccurrence(booking, request.Reason);

            // Every booking ACTUALLY cancelled by this call (the primary, plus any cascaded sibling) gets
            // exactly one cancellation notification - one idempotent outbox item per cancelled occurrence,
            // never one for the whole series, so a future sender's per-booking message stays consistent
            // with every other booking lifecycle notification. Tracked here as the source of what to
            // enqueue below.
            var cancelledBookings = new List<Booking> { booking };

            // Every OTHER occurrence in the same series that starts at or after this one, is still in a
            // cancellable status, and has NOT been checked into - never occurrences that already ran their
            // course (Completed/NoShow), were already Rejected, predate this one, or were already checked
            // in (same "never retroactively cancel a checked-in occurrence" rule as the primary target
            // above, just silently excluded from the cascade exactly like an already-Cancelled/Rejected/
            // Completed sibling already is, rather than aborting the whole batch - there is no scenario
            // where this loop throws partway through, so no partial-cascade state can ever result). A
            // booking with no SeriesId (a one-off booking) has nothing to cascade into, regardless of the flag.
            if (request.CancelRemainingSeries && booking.SeriesId is { } seriesId)
            {
                var seriesOccurrences = await bookingRepository.GetBySeriesIdAsync(seriesId, cancellationToken);
                foreach (var occurrence in seriesOccurrences)
                {
                    if (occurrence.Id == booking.Id || occurrence.StartUtc < booking.StartUtc ||
                        !CancellableStatuses.Contains(occurrence.Status) || occurrence.CheckedInAtUtc is not null)
                    {
                        continue;
                    }

                    CancelOccurrence(occurrence, request.Reason);
                    cancelledBookings.Add(occurrence);
                    cascadedIds.Add(occurrence.Id);
                }
            }

            // EnqueueManyAsync performs exactly ONE SaveChangesAsync covering the primary AND every
            // cascaded sibling's Cancelled mutation (all staged above) together with every cancellation
            // outbox row - either the whole cancellation and all its notifications land, or none of it
            // does. This is the SAME transaction IResourceBookingLock opened above (no separate
            // bookingRepository.SaveChangesAsync call, and no nested transaction here) - a failure here
            // rolls back through the lock's own catch-all rollback, exactly like Approve/Reject/Check-in.
            await notificationOutboxWriter.EnqueueManyAsync(
                cancelledBookings.Select(BookingNotificationFactory.Cancellation).ToList(), cancellationToken);
        }

        return new CancelBookingResponse(booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status, cascadedIds);
    }

    private void CancelOccurrence(Booking occurrence, string? reason)
    {
        occurrence.Status = BookingStatus.Cancelled;
        occurrence.CancelledAtUtc = DateTimeOffset.UtcNow;
        occurrence.CancelledByUserId = currentUserContext.UserId;
        occurrence.CancellationReason = reason;
    }
}
