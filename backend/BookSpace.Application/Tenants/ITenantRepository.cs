using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Tenants;

// Deliberately a SEPARATE interface from BookSpace.Application.Bookings.ITenantRepository, not an
// extension of it - that one is explicitly documented as "minimal, read-only, and deliberately narrow...
// add a broader ITenantRepository only when a feature actually needs one," scoped to exactly what the
// booking/approval workflow needs (Tenant.ApprovalExpiryHours by id). This is that broader repository,
// scoped to exactly what tenant-onboarding and tenant lifecycle management need instead. The same
// concrete Infrastructure class implements both interfaces (see TenantRepository) rather than two
// independent database-access classes.
public interface ITenantRepository
{
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken);

    // Unfiltered by design: Tenant is not ITenantOwned (it IS the tenant, not owned by one - see
    // BookSpace.Application.Bookings.ITenantRepository's own implementation for the same shape), and a
    // SysAdmin resending a pending invitation (or managing a tenant's lifecycle generally) is, by this
    // feature's own nature, always acting on a tenant that is never their own ambient
    // ICurrentUserContext.TenantId - the global SysAdmin has none.
    Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    // The SysAdmin tenant list - every Tenant, never filtered by the caller's own tenant (there is none).
    // search matches Name (case-insensitivity depends on database collation, same as every other text
    // lookup in this codebase); status restricts to that exact TenantStatus. Ordered by CreatedAtUtc
    // descending then Id (a stable tiebreaker for rows created in the same instant) so a given page's
    // contents don't shift under a caller paging through while new tenants are created concurrently.
    Task<PagedResult<Tenant>> GetPagedAsync(
        int page, int pageSize, string? search, TenantStatus? status, CancellationToken cancellationToken);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
