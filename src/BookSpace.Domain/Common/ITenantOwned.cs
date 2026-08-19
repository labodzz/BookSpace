namespace BookSpace.Domain.Common;

public interface ITenantOwned
{
    Guid TenantId { get; }
}
