using BookSpace.Application.Auth;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the global tenant query filter (BookSpaceDbContext.ApplyTenantFilter) fails CLOSED - a
// missing tenant context must see zero tenant-owned rows, never every tenant's rows - and that the
// one sanctioned exception (UserRepository.FindByEmailAsync, used pre-authentication during login)
// still works via its own explicit IgnoreQueryFilters() rather than relying on the filter itself.
// Runs against real SQL Server (LocalDB), matching SaveChangesHandlingConflictsAsyncTests - a
// throwaway per-test-run database, not the shared dev one.
public sealed class TenantIsolationFilterTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceTenantFilterTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;

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
