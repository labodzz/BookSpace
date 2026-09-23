using BookSpace.Application.Common;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the fix for a race in RemoveResourceApproverCommandHandler's last-approver guard, wrapped in
// the same IResourceBookingLock boundary UpdateResourceCapacityConcurrencyTests proves for capacity
// reduction - see docs/resource-lifecycle-and-capacity.md. Before this fix, the handler counted
// remaining approvers with no lock at all: two admins removing two DIFFERENT approvers from the same
// two-approver, RequiresApproval=true resource at the same moment could both read "2 remaining" before
// either committed, both pass the <=1 check, and both succeed - leaving zero approvers despite the
// guard existing. Requires real SQL Server (LocalDB): SQLite has no UPDLOCK/HOLDLOCK support, so
// IResourceBookingLock is a no-op there and this race cannot be proven against it.
public sealed class RemoveResourceApproverConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceRemoveApproverConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _approverAUserId;
    private Guid _approverBUserId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _approverAUserId = Guid.NewGuid();
        _approverBUserId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Remove Approver Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User { Id = _approverAUserId, TenantId = _tenantId, FirstName = "Approver", LastName = "A", Email = "approver-a@remove-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _approverBUserId, TenantId = _tenantId, FirstName = "Approver", LastName = "B", Email = "approver-b@remove-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // The exact race the fix closes: a resource with exactly two approvers, RequiresApproval=true. Two
    // admins each try to remove a DIFFERENT one of the two approvers at the same moment. Whichever
    // removal wins the resource lock must commit; the other must re-read the now-current approver count
    // (1, not 2) and be rejected - the resource must never end up with zero approvers, regardless of
    // which removal happened to win.
    [Fact]
    public async Task ConcurrentRemovalOfTwoDifferentApprovers_ExactlyOneSucceedsAndTheResourceKeepsAnApprover()
    {
        var resourceId = await SeedResourceRequiringApprovalAsync();
        await SeedResourceApproverAsync(resourceId, _approverAUserId);
        await SeedResourceApproverAsync(resourceId, _approverBUserId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> RemoveAsync(Guid userId)
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId);
            var handler = CreateHandler(dbContext);
            try
            {
                await handler.Handle(new RemoveResourceApproverCommandRequest(resourceId, userId), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var taskA = Task.Run(() => RemoveAsync(_approverAUserId));
        var taskB = Task.Run(() => RemoveAsync(_approverBUserId));
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext(_tenantId);
        var remainingApprovers = await verifyContext.ResourceApprovers.Where(a => a.ResourceId == resourceId).ToListAsync();
        Assert.Single(remainingApprovers); // never left at zero, regardless of which removal won
    }

    [Fact]
    public async Task RemoveApprover_WhenAnotherRemains_Succeeds()
    {
        var resourceId = await SeedResourceRequiringApprovalAsync();
        await SeedResourceApproverAsync(resourceId, _approverAUserId);
        await SeedResourceApproverAsync(resourceId, _approverBUserId);

        await using var dbContext = CreateDbContext(_tenantId);
        var handler = CreateHandler(dbContext);
        await handler.Handle(new RemoveResourceApproverCommandRequest(resourceId, _approverAUserId), CancellationToken.None);

        await using var verifyContext = CreateDbContext(_tenantId);
        var remainingApprovers = await verifyContext.ResourceApprovers.Where(a => a.ResourceId == resourceId).ToListAsync();
        Assert.Single(remainingApprovers);
        Assert.Equal(_approverBUserId, remainingApprovers[0].UserId);
    }

    [Fact]
    public async Task RemoveApprover_WhenItIsTheLastOne_ThrowsConflictExceptionAndLeavesItInPlace()
    {
        var resourceId = await SeedResourceRequiringApprovalAsync();
        await SeedResourceApproverAsync(resourceId, _approverAUserId);

        await using var dbContext = CreateDbContext(_tenantId);
        var handler = CreateHandler(dbContext);

        await Assert.ThrowsAsync<ConflictException>(
            () => handler.Handle(new RemoveResourceApproverCommandRequest(resourceId, _approverAUserId), CancellationToken.None));

        await using var verifyContext = CreateDbContext(_tenantId);
        Assert.Single(await verifyContext.ResourceApprovers.Where(a => a.ResourceId == resourceId).ToListAsync());
    }

    private RemoveResourceApproverCommandHandler CreateHandler(BookSpaceDbContext dbContext) => new(
        new ResourceApproverRepository(dbContext),
        new ResourceRepository(dbContext),
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance));

    private async Task<Guid> SeedResourceRequiringApprovalAsync()
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Remove Approver Room {resourceId:N}",
            Capacity = 4, RequiresApproval = true, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private async Task SeedResourceApproverAsync(Guid resourceId, Guid approverUserId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = approverUserId });
        await dbContext.SaveChangesAsync();
    }

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
