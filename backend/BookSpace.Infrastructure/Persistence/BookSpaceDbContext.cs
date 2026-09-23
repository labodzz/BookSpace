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
    public DbSet<Invitation> Invitations => Set<Invitation>();
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
        // SQL Server's rowversion/timestamp type is server-generated and enforces true optimistic
        // concurrency; SQLite (used by the integration test suite for speed) has no equivalent, so
        // IsRowVersion() there produces a NOT NULL column nothing ever populates. Real concurrency-
        // conflict behavior is instead proven against real SQL Server (see
        // RefreshTokenRotationConcurrencyTests) - on SQLite the column is just an ordinary,
        // manually-defaulted byte[] so CRUD keeps working, without pretending to guard anything.
        builder.ConfigureBookSpaceModel(useRowVersionColumns: Database.IsSqlServer());
        ApplyTenantIsolationFilters(builder);
    }

    // The structural half of tenant isolation: every ITenantOwned entity gets a query filter baked
    // into the model itself, so no LINQ query against it can return another tenant's rows - not
    // because a developer remembered a .Where(), but because EF Core injects the predicate before the
    // query ever runs. currentUserContext.TenantId is read fresh per query (it's a field on this
    // DbContext instance, and a new instance is created per request), so this stays correct per
    // request even though OnModelCreating itself only runs once per DbContext type.
    //
    // Fails CLOSED when there is no tenant context: a null TenantId matches nothing (see
    // ApplyTenantFilter below), not everything. A normal authenticated request always has a
    // tenant_id claim, so this only bites a code path that forgot to resolve one - exactly the
    // "accidentally unscoped" case this filter exists to prevent. The one legitimate reason to read
    // an ITenantOwned entity with no tenant context yet is looking a user up by email during login
    // (email is globally unique, not per-tenant, and the tenant isn't known until after that lookup
    // succeeds) - that path uses IgnoreQueryFilters() explicitly instead of relying on this filter
    // failing open (see UserRepository.FindByEmailAsync).
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
        builder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == currentUserContext.TenantId);
    }
}
