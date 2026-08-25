using System.Reflection;
using BookSpace.Application.Security;
using BookSpace.Domain.Common;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

public sealed class BookSpaceDbContext(DbContextOptions<BookSpaceDbContext> options, ICurrentUserContext currentUserContext)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<BlackoutPeriod> BlackoutPeriods => Set<BlackoutPeriod>();
    public DbSet<ResourceApprover> ResourceApprovers => Set<ResourceApprover>();
    public DbSet<RecurringSeries> RecurringSeries => Set<RecurringSeries>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ConfigureBookSpaceModel();
        ApplyTenantIsolationFilters(builder);
    }

    // The structural half of tenant isolation: every ITenantOwned entity gets a query filter baked
    // into the model itself, so no LINQ query against it can return another tenant's rows - not
    // because a developer remembered a .Where(), but because EF Core injects the predicate before the
    // query ever runs. currentUserContext.TenantId is read fresh per query (it's a field on this
    // DbContext instance, and a new instance is created per request), so this stays correct per
    // request even though OnModelCreating itself only runs once per DbContext type.
    private void ApplyTenantIsolationFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
        }
    }

    private static readonly MethodInfo ApplyTenantFilterMethod = typeof(BookSpaceDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, ITenantOwned
    {
        builder.Entity<TEntity>().HasQueryFilter(entity =>
            currentUserContext.TenantId == null || entity.TenantId == currentUserContext.TenantId);
    }
}
