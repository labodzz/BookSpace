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
