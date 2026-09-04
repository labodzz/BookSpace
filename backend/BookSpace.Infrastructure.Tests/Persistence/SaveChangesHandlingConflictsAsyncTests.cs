using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Exercises SaveChangesHandlingConflictsAsync against a REAL SQL Server (LocalDB), not SQLite. The
// method's entire job is catching Microsoft.Data.SqlClient.SqlException error codes 2601/2627, which
// only a real SQL Server ever throws - SQLite raises a different exception type entirely, so the rest
// of this repo's integration suite (which deliberately runs on SQLite to stay fast/dependency-free)
// can never verify this specific code path. This is the one test that genuinely needs LocalDB; it
// creates and tears down its own throwaway database rather than touching the shared dev "BookSpace" one.
public sealed class SaveChangesHandlingConflictsAsyncTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Name = "Concurrency Test Tenant",
            DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task SaveChangesHandlingConflictsAsync_WithConcurrentDuplicateNames_ExactlyOneThrowsConflictException()
    {
        var name = $"Race Room {Guid.NewGuid()}";

        async Task<bool> TryInsertAsync()
        {
            await using var dbContext = CreateDbContext();
            dbContext.Resources.Add(new Resource
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ResourceTypeId = _resourceTypeId,
                Name = name,
                Capacity = 1,
                RequiresApproval = false,
                Status = ResourceStatus.Active,
                TimeZoneId = "UTC",
            });

            try
            {
                await dbContext.SaveChangesHandlingConflictsAsync(CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryInsertAsync(), TryInsertAsync());

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(1, results.Count(succeeded => !succeeded));

        await using var verifyContext = CreateDbContext();
        var savedCount = await verifyContext.Resources.CountAsync(resource => resource.Name == name);
        Assert.Equal(1, savedCount);
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(_tenantId));
    }

    // The tenant filter fails closed on a null TenantId (see BookSpaceDbContext.ApplyTenantFilter), so
    // this test's own verification read needs a real tenant context, not null - inserts wouldn't have
    // cared (query filters never touch writes), but the CountAsync check below is a read.
    private sealed class FixedCurrentUserContext(Guid tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
