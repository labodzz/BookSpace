using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves EmailRecipientResolver's tenant-isolation guarantee against a REAL SQL Server (LocalDB) - the
// global query filter itself is the thing under test here (via IgnoreQueryFilters()), so a real
// BookSpaceDbContext against a real database is the only way to prove it, not a mock.
public sealed class EmailRecipientResolverTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceEmailRecipientResolverTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _userInTenantAId;
    private Guid _userWithNoEmailId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        _userInTenantAId = Guid.NewGuid();
        _userWithNoEmailId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Resolver Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Resolver Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User
        {
            Id = _userInTenantAId, TenantId = _tenantAId, FirstName = "Ana", LastName = "Anic",
            Email = "ana@bookspace.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        dbContext.Users.Add(new User
        {
            Id = _userWithNoEmailId, TenantId = _tenantAId, FirstName = "No", LastName = "Email",
            Email = "", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ResolveAsync_ForAnExistingUserInTheirOwnTenant_ReturnsTheirEmailAndDisplayName()
    {
        var result = await ResolveAsync(_tenantAId, _userInTenantAId);

        Assert.NotNull(result);
        Assert.Equal("ana@bookspace.test", result.Email);
        Assert.Equal("Ana Anic", result.DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_ForTheSameUserIdUnderADifferentTenant_ReturnsNull()
    {
        var result = await ResolveAsync(_tenantBId, _userInTenantAId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ForANonexistentUser_ReturnsNull()
    {
        var result = await ResolveAsync(_tenantAId, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ForAUserWithNoEmailAddress_ReturnsNull()
    {
        var result = await ResolveAsync(_tenantAId, _userWithNoEmailId);

        Assert.Null(result);
    }

    private async Task<BookSpace.Application.Notifications.EmailRecipient?> ResolveAsync(Guid tenantId, Guid recipientUserId)
    {
        await using var dbContext = CreateDbContext();
        var resolver = new BookSpace.Infrastructure.Persistence.EmailRecipientResolver(
            dbContext, NullLogger<BookSpace.Infrastructure.Persistence.EmailRecipientResolver>.Instance);
        return await resolver.ResolveAsync(tenantId, recipientUserId, CancellationToken.None);
    }

    private BookSpace.Infrastructure.Persistence.BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpace.Infrastructure.Persistence.BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpace.Infrastructure.Persistence.BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }
}
