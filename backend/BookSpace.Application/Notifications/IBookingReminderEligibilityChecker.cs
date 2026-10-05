namespace BookSpace.Application.Notifications;

// The cancellation-race guard a Booking.Reminder send must run immediately before actually dispatching -
// see docs/background-jobs.md ("Cancellation race for reminders"). A reminder can sit enqueued for a
// while before the dispatcher ever picks it up; in that window the booking it was about may have been
// cancelled or rejected (or, for a reminder enqueued very close to its lead time, may have already
// started). None of that is visible to the reminder producer at enqueue time - this is the one, narrow,
// documented exception to the global tenant query filter for this check (same justification as
// IEmailRecipientResolver/IBookingReminderReader): background processing has no HTTP request/JWT claim to
// scope a single tenant by, and this re-check must still never let a (tenantId, bookingId) pair resolve
// to a different tenant's row.
public interface IBookingReminderEligibilityChecker
{
    // True only when the booking still exists, still belongs to tenantId, is still Confirmed, and its
    // StartUtc has not yet passed - false for every other case (not found, wrong tenant, no longer
    // Confirmed, already started), which the caller (the production INotificationSender) must treat as
    // "suppress this reminder" rather than retry.
    Task<bool> IsStillEligibleAsync(Guid tenantId, Guid bookingId, CancellationToken cancellationToken);
}
