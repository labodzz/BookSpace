using BookSpace.Domain.Entities;

namespace BookSpace.Application.Bookings;

// Minimal, read-only, and deliberately narrow - the only thing the booking/approval workflow needs is
// Tenant.ApprovalExpiryHours (to compute a new ApprovalRequest.ExpiresAtUtc). Full Tenant CRUD is out of
// scope here; add a broader ITenantRepository only when a feature actually needs one.
public interface ITenantRepository
{
    Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
