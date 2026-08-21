using BookSpace.Api.Domain.Enums;

namespace BookSpace.Api.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefaultTimeZoneId { get; set; } = "UTC";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public int ReminderLeadMinutes { get; set; } = 30;
    public int NoShowGraceMinutes { get; set; } = 15;
    public int ApprovalExpiryHours { get; set; } = 48;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
