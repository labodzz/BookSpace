using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Real SQL Server (LocalDB), matching every other test in this folder. Users is read through
// IgnoreQueryFilters() throughout (ProductionBootstrapper relies on the same no-tenant-context path
// UserRepository.FindByEmailAsync uses for the same reason - see BookSpaceDbContext's own comment on
// ApplyUserTenantFilter) - the null TenantId in FixedCurrentUserContext below is what a real startup
// context looks like (no HttpContext yet), and this proves that path for real rather than assuming an
// EnsureCreated-only in-memory provider behaves the same way. It is also exactly what the seeded
// SysAdmin's own row looks like once seeded: TenantId stays null forever, not just at read time.
public sealed class ProductionBootstrapperTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceBootstrapTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly PasswordHasher PasswordHasher = new();

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task SeedAsync_WhenDisabled_CreatesNoTenantOrUser()
    {
        await using var dbContext = CreateDbContext();

        await ProductionBootstrapper.SeedAsync(dbContext, PasswordHasher, CreateOptions(enabled: false), NullLogger.Instance);

        Assert.Equal(0, await dbContext.Tenants.CountAsync());
        Assert.Equal(0, await dbContext.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SeedAsync_WhenEnabledButMissingARequiredValue_ThrowsWithoutWritingAnything()
    {
        await using var dbContext = CreateDbContext();
        var options = CreateOptions(sysAdminPassword: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProductionBootstrapper.SeedAsync(dbContext, PasswordHasher, options, NullLogger.Instance));

        Assert.Equal(0, await dbContext.Tenants.CountAsync());
        Assert.Equal(0, await dbContext.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SeedAsync_OnAnEmptyDatabase_CreatesOnlyAGlobalSysAdminAndNoTenant()
    {
        await using var dbContext = CreateDbContext();
        var options = CreateOptions();

        await ProductionBootstrapper.SeedAsync(dbContext, PasswordHasher, options, NullLogger.Instance);

        Assert.Equal(0, await dbContext.Tenants.CountAsync());

        var sysAdmin = await dbContext.Users.IgnoreQueryFilters().SingleAsync();
        Assert.Null(sysAdmin.TenantId);
        Assert.Equal(options.SysAdminEmail, sysAdmin.Email);
        Assert.Equal(UserStatus.Active, sysAdmin.Status);
        Assert.True(PasswordHasher.Verify(sysAdmin.PasswordHash, options.SysAdminPassword!));

        var role = await dbContext.Roles.SingleAsync();
        Assert.Equal("SysAdmin", role.Name);
        var userRole = await dbContext.UserRoles.SingleAsync();
        Assert.Equal(sysAdmin.Id, userRole.UserId);
        Assert.Equal(role.Id, userRole.RoleId);
    }

    // The exact scenario the requirement calls out by name: a redeploy/restart with the same
    // Bootstrap:* configuration must not recreate the SysAdmin. Two independent DbContext instances
    // (not one reused across both calls) so this proves it against a real "next process start" query,
    // not something the first call's own change tracker could be quietly propping up.
    [Fact]
    public async Task SeedAsync_CalledAgainAfterARestart_IsIdempotent()
    {
        var options = CreateOptions();

        await using (var firstRun = CreateDbContext())
        {
            await ProductionBootstrapper.SeedAsync(firstRun, PasswordHasher, options, NullLogger.Instance);
        }

        await using (var secondRun = CreateDbContext())
        {
            await ProductionBootstrapper.SeedAsync(secondRun, PasswordHasher, options, NullLogger.Instance);
        }

        await using var verifyContext = CreateDbContext();
        Assert.Equal(0, await verifyContext.Tenants.CountAsync());
        Assert.Equal(1, await verifyContext.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SeedAsync_WhenASysAdminRoleAlreadyExists_ReusesItInsteadOfCreatingADuplicate()
    {
        Guid existingRoleId;
        await using (var seedContext = CreateDbContext())
        {
            var existingRole = new Role { Id = Guid.NewGuid(), Name = "SysAdmin" };
            seedContext.Roles.Add(existingRole);
            await seedContext.SaveChangesAsync();
            existingRoleId = existingRole.Id;
        }

        await using var dbContext = CreateDbContext();

        await ProductionBootstrapper.SeedAsync(dbContext, PasswordHasher, CreateOptions(), NullLogger.Instance);

        Assert.Equal(1, await dbContext.Roles.CountAsync(role => role.Name == "SysAdmin"));
        var userRole = await dbContext.UserRoles.SingleAsync();
        Assert.Equal(existingRoleId, userRole.RoleId);
    }

    private static BootstrapOptions CreateOptions(
        bool enabled = true,
        string? sysAdminFirstName = "First",
        string? sysAdminLastName = "Admin",
        string? sysAdminEmail = "sysadmin@bootstrap.test",
        string? sysAdminPassword = "SuperSecret123!") => new()
    {
        Enabled = enabled,
        SysAdminFirstName = sysAdminFirstName,
        SysAdminLastName = sysAdminLastName,
        SysAdminEmail = sysAdminEmail,
        SysAdminPassword = sysAdminPassword,
    };

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }
}
