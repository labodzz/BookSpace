using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

public sealed class ResourceType : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class Resource : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Capacity { get; set; }
    public bool RequiresApproval { get; set; }
    public ResourceStatus Status { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;

    // SQL Server rowversion, EF-managed - detects two concurrent updates (or an update racing an
    // archive) loaded from the same starting state, so the loser gets a 409 instead of silently
    // overwriting the winner's change.
    public byte[] RowVersion { get; set; } = [];
}

public sealed class AvailabilityRule : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

public sealed class BlackoutPeriod : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string Reason { get; set; } = string.Empty;

    // SQL Server rowversion, EF-managed - see Resource.RowVersion for why.
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ResourceApprover : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
}
