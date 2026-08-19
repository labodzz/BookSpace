namespace BookSpace.Application.Abstractions;

public interface ITenantContext
{
    Guid TenantId { get; }
    bool IsPlatformOperator { get; }
}
