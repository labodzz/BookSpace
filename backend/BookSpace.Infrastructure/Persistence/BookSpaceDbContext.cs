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
    public DbSet<CalendarSubscription> CalendarSubscriptions => Set<CalendarSubscription>();
    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<BlackoutPeriod> BlackoutPeriods => Set<BlackoutPeriod>();
    public DbSet<ResourceApprover> ResourceApprovers => Set<ResourceApprover>();
    public DbSet<RecurringSeries> RecurringSeries => Set<RecurringSeries>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<JobLease> JobLeases => Set<JobLease>();
    public DbSet<NotificationOutboxItem> NotificationOutboxItems => Set<NotificationOutboxItem>();

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
        ApplyUserTenantFilter(builder);
    }

    // User is deliberately NOT ITenantOwned (see User.cs), so the generic loop below skips it - but it
    // still needs the identical tenant-scoping behavior for every tenant-scoped read (GetUsers, role
    // management, administrator-count checks, ...). This filter is registered by hand instead, rather
    // than reusing ApplyTenantFilter<TEntity> below, because User.TenantId is the ONE place in this whole
    // schema a tenant-owning column is nullable - and that breaks the "SQL NULL = NULL -> UNKNOWN" fail-
    // closed reasoning every other filter in this file relies on: EF Core's default query translation
    // does NOT use raw SQL null comparison for a `Guid? == Guid?` predicate - it rewrites it using C#
    // comparison semantics, i.e. `(a == b) OR (a IS NULL AND b IS NULL)`, which means a global SysAdmin
    // (User.TenantId == null) querying with currentUserContext.TenantId also null would match EVERY
    // global SysAdmin row (anyone else with TenantId == null), not nothing - confirmed by
    // TenantOnboardingEndToEndTests' own "SysAdmin sees nothing through the ordinary filtered Users
    // query" step before this fix was added. The explicit `currentUserContext.TenantId != null` guard
    // below short-circuits that: when there is no ambient tenant, the whole predicate is false for every
    // row, including the caller's own. When there IS an ambient tenant (a real, non-null Guid), comparing
    // it to User.TenantId needs no such rewrite - a non-null value is never "null-equal" to anything -
    // so tenant users keep working exactly as before. The sanctioned bypasses (login, refresh, accept-
    // invitation, the SysAdmin tenant-onboarding lookup, tenant user aggregates) go through UserRepository
    // methods that call IgnoreQueryFilters() explicitly instead - see UserRepository.cs.
    private void ApplyUserTenantFilter(ModelBuilder builder)
    {
        builder.Entity<User>().HasQueryFilter(user => currentUserContext.TenantId != null && user.TenantId == currentUserContext.TenantId);
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
