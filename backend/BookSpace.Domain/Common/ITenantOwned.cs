namespace BookSpace.Domain.Common;

// Every ownable entity implements this. It is the one, unambiguous way tenancy is represented across
// the model - a direct, required TenantId column, never an inferred/joined-through ownership.
public interface ITenantOwned
{
    Guid TenantId { get; }
}
