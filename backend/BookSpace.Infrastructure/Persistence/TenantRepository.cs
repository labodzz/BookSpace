using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// Implements BOTH BookSpace.Application.Bookings.ITenantRepository (the pre-existing, deliberately narrow,
// read-only interface the booking/approval workflow depends on) AND BookSpace.Application.Tenants.
// ITenantRepository (the broader one tenant onboarding needs - see that interface's own doc comment for
// why it is a separate interface rather than an extension of the narrow one). One concrete database-access
// class backs both, rather than two independent ones, since both are ultimately just "read/write the
// Tenants table" - only the interfaces callers depend on are kept narrow and feature-scoped.
internal sealed class TenantRepository(BookSpaceDbContext dbContext) :
    BookSpace.Application.Bookings.ITenantRepository, BookSpace.Application.Tenants.ITenantRepository
{
    public Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Tenants.FirstOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);

    public async Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken) =>
        await dbContext.Tenants.AnyAsync(tenant => tenant.Name == name, cancellationToken);

    public async Task<PagedResult<Tenant>> GetPagedAsync(
        int page, int pageSize, string? search, TenantStatus? status, CancellationToken cancellationToken)
    {
        var query = dbContext.Tenants.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(tenant => tenant.Name.Contains(search));
        }

        if (status is not null)
        {
            query = query.Where(tenant => tenant.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // SQLite (fast integration tests only) cannot translate ORDER BY on a DateTimeOffset column into
        // SQL - the same limitation BookingRepository.GetOwnBookingsAsync already works around - so
        // ordering falls back to an in-memory pass there; a platform's tenant count is never large enough
        // for that to matter. SQL Server orders and pages in the database as usual.
        if (dbContext.Database.IsSqlServer())
        {
            var items = await query
                .OrderByDescending(tenant => tenant.CreatedAtUtc)
                .ThenBy(tenant => tenant.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            return new PagedResult<Tenant>(items, page, pageSize, totalCount);
        }

        var allItems = await query.ToListAsync(cancellationToken);
        var pagedItems = allItems
            .OrderByDescending(tenant => tenant.CreatedAtUtc)
            .ThenBy(tenant => tenant.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return new PagedResult<Tenant>(pagedItems, page, pageSize, totalCount);
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken) =>
        await dbContext.Tenants.AddAsync(tenant, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
