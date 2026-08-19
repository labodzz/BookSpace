using BookSpace.Domain.Bookings;
using BookSpace.Domain.Common;
using BookSpace.Domain.Notifications;

namespace BookSpace.Domain.Entities;

public sealed class Booking : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SeriesId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public BookingStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class RecurringSeries : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public DateTime StartLocal { get; set; }
    public TimeSpan Duration { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; }
    public DateOnly? UntilDate { get; set; }
    public int? OccurrenceCount { get; set; }
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
    public DateTimeOffset? DecisionDateUtc { get; set; }
}

public sealed class Notification : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public NotificationStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset ScheduledForUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class RefreshToken : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
