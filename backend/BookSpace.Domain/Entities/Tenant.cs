using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DefaultTimeZoneId { get; set; } = "UTC";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public int ReminderLeadMinutes { get; set; } = 30;
    public int NoShowGraceMinutes { get; set; } = 15;
    public int ApprovalExpiryHours { get; set; } = 48;

    // How many minutes before Booking.StartUtc a Confirmed booking's check-in window opens - see
    // docs/bookings-and-concurrency.md ("Booking check-in"). Mirrors ReminderLeadMinutes/NoShowGraceMinutes:
    // a per-tenant business-rule duration lives directly on Tenant, not in BackgroundJobsOptions (which only
    // ever configures background-job orchestration, not live, user-facing booking rules). The window's
    // CLOSING edge reuses NoShowGraceMinutes rather than a second new setting - see CheckInEligibilityChecker.
    public int CheckInLeadMinutes { get; set; } = 15;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
