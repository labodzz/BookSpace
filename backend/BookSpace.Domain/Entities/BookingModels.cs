using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

public sealed class RecurringSeries : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; }
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

public sealed class ApprovalRequest : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public Guid ApproverId { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
