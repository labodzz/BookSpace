using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// Wall-clock shape (StartDate/StartTime/EndTime), not a fixed UTC instant - deliberately mirrors
// AvailabilityRule's convention, because a recurring occurrence's local time of day must be re-resolved
// to UTC independently for each generated date to stay DST-correct (see
// docs/recurring-bookings-and-approvals.md). A single stored UTC instant plus AddDays/AddMonths math
// cannot express that. TimeZoneId is a snapshot of Resource.TimeZoneId at series-creation time - never
// client-suppliable - so later Resource timezone changes don't retroactively reinterpret already-
// generated occurrences.
public sealed class RecurringSeries : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly StartDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; }

    // End condition: exactly one of these two is set (CK_RecurringSeries_EndCondition).
    public DateOnly? EndDate { get; set; }
    public int? OccurrenceCount { get; set; }

    public int Quantity { get; set; } = 1;
}

public sealed class Booking : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SeriesId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public int Quantity { get; set; } = 1;
    public BookingStatus Status { get; set; }
    public DateTimeOffset? CheckedInAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    // Set the moment a Booking.Reminder outbox item was successfully enqueued for this booking - NOT when
    // (or whether) that reminder was actually sent. See docs/background-jobs.md ("Reminder job - the
    // ReminderQueuedAtUtc marker") for the full design: this exists purely so BookingReminderReader's due
    // query can exclude already-enqueued bookings server-side (an index-supported `IS NULL` check), instead
    // of relying only on the (TenantId, IdempotencyKey) unique constraint to reject a re-submitted
    // duplicate - a constraint alone stops a second row from being created, but does nothing to stop the
    // SAME already-handled booking from being re-selected as a "due" candidate on every subsequent poll,
    // starving every other booking behind it once the batch fills up with already-enqueued rows. Set in the
    // SAME SaveChangesAsync call that creates the outbox row (see ReminderProducer/IBookingReminderMarker),
    // so the two are never observably out of sync - either both land or neither does. Once set, it is never
    // cleared: a later Sent/Suppressed/DeadLettered outcome for that reminder, or the booking itself later
    // being cancelled, does not reset it and does not cause a second reminder to be enqueued.
    public DateTimeOffset? ReminderQueuedAtUtc { get; set; }
}

// ApproverId is nullable and populated only at decision time, with whoever actually approved/rejected -
// not pre-assigned at request time. Any ResourceApprover for the booking's resource (or TenantAdmin/
// SysAdmin) may act on a Pending request; there is no fixed single assignee to pre-populate.
public sealed class ApprovalRequest : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public Guid? ApproverId { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
