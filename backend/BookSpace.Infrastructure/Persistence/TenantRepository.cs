using BookSpace.Application.Bookings;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class TenantRepository(BookSpaceDbContext dbContext) : ITenantRepository
{
    public Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Tenants.FirstOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);
}
