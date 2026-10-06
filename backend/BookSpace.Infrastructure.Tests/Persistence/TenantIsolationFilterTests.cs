using BookSpace.Application.Auth;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the global tenant query filter (BookSpaceDbContext.ApplyTenantFilter, and User's own
// ApplyUserTenantFilter) fails CLOSED - a missing tenant context must see zero tenant-owned rows (and
// zero global-SysAdmin rows, including the caller's own), never every tenant's/every SysAdmin's rows -
// and that the sanctioned exceptions (UserRepository.FindByEmailAsync/FindByIdForAuthenticationAsync,
// used pre-authentication during login/refresh) still work via their own explicit IgnoreQueryFilters()
// rather than relying on the filter itself. Runs against real SQL Server (LocalDB), matching
// SaveChangesHandlingConflictsAsyncTests - a throwaway per-test-run database, not the shared dev one.
public sealed class TenantIsolationFilterTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceTenantFilterTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _sysAdminOneId;
    private Guid _sysAdminTwoId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null);
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        var resourceTypeId = Guid.NewGuid();

        dbContext.Tenants.AddRange(
            new Tenant { Id = _tenantAId, Name = "Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Tenant { Id = _tenantBId, Name = "Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
        dbContext.ResourceTypes.Add(new ResourceType { Id = resourceTypeId, TenantId = _tenantAId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAId,
            ResourceTypeId = resourceTypeId,
            Name = "Tenant A Room",
            Capacity = 1,
            RequiresApproval = false,
            Status = ResourceStatus.Active,
            TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantAId,
            FirstName = "Tenant",
            LastName = "AUser",
            Email = "tenant-a-user@bookspace.test",
            PasswordHash = "irrelevant-for-this-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantBId,
            FirstName = "Tenant",
            LastName = "BUser",
            Email = "tenant-b-user@bookspace.test",
            PasswordHash = "irrelevant-for-this-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        // Two SEPARATE global SysAdmins (TenantId = null on both) - this is exactly the shape that
        // caught a real EF Core translation bug during this feature's own development (see
        // SysAdminFilter_WithNoTenantContext_SeesNoGlobalSysAdminEitherIncludingItsOwn below): a naive
        // `user.TenantId == currentUserContext.TenantId` filter, with both sides null, gets rewritten by
        // EF Core's default C# null-comparison semantics into effectively "both are null", which would
        // make every SysAdmin visible to every other SysAdmin through this ordinary filtered query.
        _sysAdminOneId = Guid.NewGuid();
        _sysAdminTwoId = Guid.NewGuid();
        dbContext.Users.Add(new User
        {
            Id = _sysAdminOneId, TenantId = null, FirstName = "Sys", LastName = "AdminOne",
            Email = "sysadmin-one@bookspace.test", PasswordHash = "irrelevant-for-this-test", CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.Users.Add(new User
        {
            Id = _sysAdminTwoId, TenantId = null, FirstName = "Sys", LastName = "AdminTwo",
            Email = "sysadmin-two@bookspace.test", PasswordHash = "irrelevant-for-this-test", CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null);
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TenantFilter_WithMatchingTenantContext_ReturnsOnlyThatTenantsResources()
    {
        await using var dbContext = CreateDbContext(_tenantAId);

        var resources = await dbContext.Resources.ToListAsync();

        Assert.Single(resources);
    }

    [Fact]
    public async Task TenantFilter_WithNoTenantContext_ReturnsNoTenantOwnedRows()
    {
        await using var dbContext = CreateDbContext(tenantId: null);

        var resources = await dbContext.Resources.ToListAsync();
        var users = await dbContext.Users.ToListAsync();

        Assert.Empty(resources);
        Assert.Empty(users);
    }

    [Fact]
    public async Task TenantFilter_WithDifferentTenantContext_DoesNotSeeOtherTenantsResources()
    {
        await using var dbContext = CreateDbContext(_tenantBId);

        var resources = await dbContext.Resources.ToListAsync();

        Assert.Empty(resources);
    }

    // The direct Users-specific counterpart to TenantFilter_WithMatchingTenantContext_.../
    // ...WithDifferentTenantContext_... above, which only ever checked Resources. GET /users' own
    // controller comment claims this endpoint "cannot leak another tenant's users" purely from the
    // global filter - this is the test that actually proves that claim for Users specifically, rather
    // than assuming it follows from the identical Resources behavior.
    [Fact]
    public async Task TenantFilter_WithMatchingTenantContext_ReturnsOnlyThatTenantsUsers()
    {
        await using var dbContext = CreateDbContext(_tenantAId);

        var users = await dbContext.Users.ToListAsync();

        var user = Assert.Single(users);
        Assert.Equal("tenant-a-user@bookspace.test", user.Email);
    }

    [Fact]
    public async Task TenantFilter_WithDifferentTenantContext_DoesNotSeeOtherTenantsUsers()
    {
        await using var dbContext = CreateDbContext(_tenantBId);

        var users = await dbContext.Users.ToListAsync();

        Assert.DoesNotContain(users, user => user.Email == "tenant-a-user@bookspace.test");
    }

    // Named explicitly (rather than just relying on TenantFilter_WithNoTenantContext_ReturnsNoTenantOwnedRows
    // above also covering it) because this is the exact scenario that caught a real bug during this
    // feature's own development - see the seed data's own comment and
    // BookSpaceDbContext.ApplyUserTenantFilter's doc comment for the EF Core null-translation detail.
    [Fact]
    public async Task SysAdminFilter_WithNoTenantContext_SeesNoGlobalSysAdminEitherIncludingItsOwn()
    {
        await using var dbContext = CreateDbContext(tenantId: null);

        var users = await dbContext.Users.ToListAsync();

        Assert.Empty(users);
        Assert.Null(await dbContext.Users.FirstOrDefaultAsync(u => u.Id == _sysAdminOneId));
        Assert.Null(await dbContext.Users.FirstOrDefaultAsync(u => u.Id == _sysAdminTwoId));
    }

    [Fact]
    public async Task FindByIdForAuthenticationAsync_WithNoTenantContext_StillFindsTheGlobalSysAdmin()
    {
        await using var dbContext = CreateDbContext(tenantId: null);
        IUserRepository repository = new UserRepository(dbContext);

        var user = await repository.FindByIdForAuthenticationAsync(_sysAdminOneId, CancellationToken.None);

        Assert.NotNull(user);
        Assert.Null(user!.TenantId);
    }

    [Fact]
    public async Task FindByEmailAsync_WithNoTenantContext_StillFindsUserViaExplicitPrivilegedPath()
    {
        await using var dbContext = CreateDbContext(tenantId: null);
        IUserRepository repository = new UserRepository(dbContext);

        var user = await repository.FindByEmailAsync("tenant-a-user@bookspace.test", CancellationToken.None);

        Assert.NotNull(user);
    }

    [Fact]
    public async Task FindByEmailAsync_WithWrongTenantContext_StillFindsUserViaExplicitPrivilegedPath()
    {
        await using var dbContext = CreateDbContext(_tenantBId);
        IUserRepository repository = new UserRepository(dbContext);

        var user = await repository.FindByEmailAsync("tenant-a-user@bookspace.test", CancellationToken.None);

        Assert.NotNull(user);
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
