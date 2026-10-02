using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Bookings;

// ApproveRemainingSeries approves this booking AND every OTHER currently-Pending occurrence in the same
// recurring series in one call - a plain Approver facing a series with, say, 100 pending occurrences
// would otherwise have to approve them one at a time. Ignored (no error) for a booking with no SeriesId,
// same convention as CancelBookingCommandRequest.CancelRemainingSeries. Unlike cancellation, an
// occurrence that no longer passes BookingEligibilityChecker (a blackout was added since, capacity
// changed, etc.) is never force-approved - it's left Pending and reported back as a conflict, so the
// approver can still look at it individually and decide (including rejecting just that one).
public sealed record ApproveBookingCommandRequest(Guid Id, string? DecisionNote, bool ApproveRemainingSeries = false) : IRequest<ApproveBookingResponse>;

// CascadedApprovedOccurrenceIds lists every OTHER occurrence also approved by this call (empty unless
// ApproveRemainingSeries was true and the booking belongs to a series). CascadedConflicts lists sibling
// occurrences ApproveRemainingSeries could NOT approve (still Pending) with why, mirroring
// CreateRecurringSeriesResponse.Conflicts's shape - never silently dropped, never force-approved.
public sealed record ApproveBookingResponse(
    Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status,
    IReadOnlyList<Guid> CascadedApprovedOccurrenceIds, IReadOnlyList<ApprovalSeriesConflictResponse> CascadedConflicts);

public sealed record ApprovalSeriesConflictResponse(Guid BookingId, string Reason);

public sealed class ApproveBookingCommandRequestValidator : AbstractValidator<ApproveBookingCommandRequest>
{
    public ApproveBookingCommandRequestValidator()
    {
        RuleFor(command => command.DecisionNote).MaximumLength(2000);
    }
}

// Availability MUST be re-checked at approval time, not trusted from creation time - see
// docs/recurring-bookings-and-approvals.md. Protected by the same IResourceBookingLock as booking
// creation and UpdateResourceCommandHandler's capacity-reduction check: the re-check and every
// Pending -> Confirmed write happen inside one locked transaction, so a concurrent create/approve/
// capacity-update for the same resource can never interleave with this decision. For the PRIMARY booking
// (request.Id), ineligibility throws before any write - it stays exactly Pending, never partially
// approved. For ApproveRemainingSeries' cascaded siblings, ineligibility does NOT throw and does NOT
// abort the batch - that sibling is simply left Pending and reported back in CascadedConflicts, so one
// occurrence going stale never blocks approving the other 99.
public sealed class ApproveBookingCommandHandler(
    IResourceBookingLock resourceBookingLock,
    IBookingRepository bookingRepository,
    IResourceRepository resourceRepository,
    IAvailabilityRuleRepository availabilityRuleRepository,
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IBookingAvailabilityRepository bookingAvailabilityRepository,
    IApprovalRequestRepository approvalRequestRepository,
    IResourceApproverRepository resourceApproverRepository,
    ICurrentUserContext currentUserContext,
    INotificationOutboxWriter notificationOutboxWriter)
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

        ApproveOccurrence(booking, approvalRequest, request.DecisionNote);

        // Every booking actually confirmed by this call (the primary, plus any cascaded sibling) gets
        // exactly one confirmation notification - tracked here as the SOURCE of what to enqueue below.
        var approvedBookings = new List<Booking> { booking };
        var cascadedApprovedIds = new List<Guid>();
        var cascadedConflicts = new List<ApprovalSeriesConflictResponse>();

        // Every other still-Pending occurrence in the same series, re-checked (never force-approved) and
        // approved inside this SAME lock acquisition - occurrences within one series never overlap each
        // other in time (each is on a distinct calendar date), so approving one can never invalidate
        // another's capacity check, exactly the same reasoning CreateRecurringSeriesCommandHandler
        // already relies on for evaluating every candidate occurrence in a single lock.
        if (request.ApproveRemainingSeries && booking.SeriesId is { } seriesId)
        {
            var seriesOccurrences = await bookingRepository.GetBySeriesIdAsync(seriesId, cancellationToken);
            var pendingSiblings = seriesOccurrences.Where(occurrence => occurrence.Id != booking.Id && occurrence.Status == BookingStatus.Pending).ToList();

            if (pendingSiblings.Count > 0)
            {
                var siblingApprovalRequests = await approvalRequestRepository.GetByBookingIdsAsync(
                    pendingSiblings.Select(occurrence => occurrence.Id).ToList(), cancellationToken);
                var siblingApprovalRequestByBookingId = siblingApprovalRequests.ToDictionary(ar => ar.BookingId);

                foreach (var occurrence in pendingSiblings)
                {
                    if (!siblingApprovalRequestByBookingId.TryGetValue(occurrence.Id, out var siblingApprovalRequest))
                    {
                        cascadedConflicts.Add(new ApprovalSeriesConflictResponse(occurrence.Id, "ApprovalRequestMissing"));
                        continue;
                    }

                    var siblingEligibility = await BookingEligibilityChecker.CheckAsync(
                        resource, occurrence.StartUtc, occurrence.EndUtc, occurrence.Quantity, excludeBookingId: occurrence.Id,
                        rules, blackouts, bookingAvailabilityRepository, cancellationToken);

                    if (siblingEligibility != BookingEligibility.Eligible)
                    {
                        cascadedConflicts.Add(new ApprovalSeriesConflictResponse(occurrence.Id, BookingEligibilityChecker.ToErrorCode(siblingEligibility)));
                        continue;
                    }

                    ApproveOccurrence(occurrence, siblingApprovalRequest, request.DecisionNote);
                    approvedBookings.Add(occurrence);
                    cascadedApprovedIds.Add(occurrence.Id);
                }
            }
        }

        // Approving always confirms at least the primary booking, so approvedBookings is never empty here -
        // unlike CreateBookingCommandHandler there is no "nothing to enqueue" branch. EnqueueAsync calls
        // SaveChangesAsync itself: the first call commits the booking/ApprovalRequest mutations for the
        // primary AND every cascaded sibling (all staged above) together with that first outbox row; each
        // further call (one per additional cascaded confirmation) commits only its own outbox row, since
        // the booking rows are already saved by then - the same accepted, narrow gap documented in
        // CreateRecurringSeriesCommandHandler for a multi-occurrence commit.
        foreach (var approvedBooking in approvedBookings)
        {
            await notificationOutboxWriter.EnqueueAsync(BookingNotificationFactory.Confirmation(approvedBooking), cancellationToken);
        }

        return new ApproveBookingResponse(
            booking.Id, booking.ResourceId, booking.StartUtc, booking.EndUtc, booking.Quantity, booking.Status,
            cascadedApprovedIds, cascadedConflicts);
    }

    private void ApproveOccurrence(Booking booking, ApprovalRequest approvalRequest, string? decisionNote)
    {
        booking.Status = BookingStatus.Confirmed;
        approvalRequest.Status = ApprovalStatus.Approved;
        approvalRequest.ApproverId = currentUserContext.UserId;
        approvalRequest.DecisionNote = decisionNote;
        approvalRequest.DecidedAtUtc = DateTimeOffset.UtcNow;
    }
}
