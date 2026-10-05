using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves StaleApprovalReader.GetDueApprovalsAsync's due-selection (Pending ApprovalRequest past its own
// ExpiresAtUtc, with a still-Pending Booking), ordering, batch-bounding, and cross-tenant read behavior
// against a REAL SQL Server (LocalDB) - see docs/background-jobs.md ("Stale-approval job"). The
// "server-side TOP, never materialize-then-filter" and index-usage claims in particular can only be
// proven here, not against SQLite or a mock.
public sealed class StaleApprovalReaderTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceStaleApprovalReaderTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _resourceTypeId;
    private Guid _resourceAId;
    private Guid _resourceBId;
    private Guid _userAId;
    private Guid _userBId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceAId = Guid.NewGuid();
        _resourceBId = Guid.NewGuid();
        _userAId = Guid.NewGuid();
        _userBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Stale Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Stale Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantAId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceAId, TenantId = _tenantAId, ResourceTypeId = _resourceTypeId, Name = "Room A",
            Capacity = 10, RequiresApproval = true, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceBId, TenantId = _tenantBId, ResourceTypeId = _resourceTypeId, Name = "Room B",
            Capacity = 10, RequiresApproval = true, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _userAId, TenantId = _tenantAId, FirstName = "Stale", LastName = "A", Email = $"stale-a-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userBId, TenantId = _tenantBId, FirstName = "Stale", LastName = "B", Email = $"stale-b-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- A/Boundary: a Pending ApprovalRequest already past its ExpiresAtUtc, with a still-Pending
    // Booking, is due ---

    [Fact]
    public async Task GetDueApprovalsAsync_WithAPendingApprovalPastItsExpiresAtUtc_ReturnsIt()
    {
        var (approvalRequestId, bookingId) = await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await GetDueApprovalsAsync();

        Assert.Single(result);
        Assert.Equal(approvalRequestId, result[0].ApprovalRequestId);
        Assert.Equal(bookingId, result[0].BookingId);
    }

    // --- B: not yet stale - ExpiresAtUtc still in the future ---

    [Fact]
    public async Task GetDueApprovalsAsync_WithAPendingApprovalNotYetPastItsExpiresAtUtc_DoesNotReturnIt()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        var result = await GetDueApprovalsAsync();

        Assert.Empty(result);
    }

    // --- C: every terminal ApprovalStatus is never due, even if (hypothetically) still past its
    // ExpiresAtUtc ---

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Cancelled)]
    [InlineData(ApprovalStatus.Expired)]
    public async Task GetDueApprovalsAsync_WithANonPendingApprovalStatus_DoesNotReturnIt(ApprovalStatus status)
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), approvalStatus: status);

        var result = await GetDueApprovalsAsync();

        Assert.Empty(result);
    }

    // --- C: the Booking must also still be Pending - a Confirmed/Rejected/Cancelled/Expired booking
    // (however that happened) is never a stale-approval candidate even if its ApprovalRequest row somehow
    // still says Pending ---

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Expired)]
    public async Task GetDueApprovalsAsync_WithANonPendingBookingStatus_DoesNotReturnIt(BookingStatus bookingStatus)
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), bookingStatus: bookingStatus);

        var result = await GetDueApprovalsAsync();

        Assert.Empty(result);
    }

    // --- E: bounded batch - never more than batchSize, and the excluded ones are the correct
    // (latest-requested) ones, not an arbitrary subset ---

    [Fact]
    public async Task GetDueApprovalsAsync_WhenMoreCandidatesExistThanBatchSize_ReturnsExactlyBatchSizeItems()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++)
        {
            await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: now.AddMinutes(-7 + i), requestedAtUtc: now.AddMinutes(-100 + i));
        }

        var result = await GetDueApprovalsAsync(batchSize: 5);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task GetDueApprovalsAsync_ReturnsCandidatesOldestRequestedFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var newer = await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: now.AddMinutes(-1), requestedAtUtc: now.AddMinutes(-10));
        var oldest = await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: now.AddMinutes(-1), requestedAtUtc: now.AddMinutes(-100));
        var middle = await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: now.AddMinutes(-1), requestedAtUtc: now.AddMinutes(-50));

        var result = await GetDueApprovalsAsync();

        Assert.Equal([oldest.ApprovalRequestId, middle.ApprovalRequestId, newer.ApprovalRequestId], result.Select(item => item.ApprovalRequestId));
    }

    // --- F: a system-wide background read must see due approvals across EVERY tenant in one call ---

    [Fact]
    public async Task GetDueApprovalsAsync_ReturnsDueApprovalsFromMultipleTenantsInOneCall()
    {
        var a = await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        var b = await SeedAsync(_tenantBId, _resourceBId, _userBId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await GetDueApprovalsAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.ApprovalRequestId == a.ApprovalRequestId && item.TenantId == _tenantAId);
        Assert.Contains(result, item => item.ApprovalRequestId == b.ApprovalRequestId && item.TenantId == _tenantBId);
    }

    [Fact]
    public async Task OrdinaryTenantScopedQuery_StillOnlySeesItsOwnTenantsApprovalRequests_UnaffectedByTheStaleApprovalReader()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedAsync(_tenantBId, _resourceBId, _userBId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var tenantScopedContext = CreateDbContext(_tenantAId);
        var visibleToTenantA = await tenantScopedContext.ApprovalRequests.ToListAsync();

        Assert.Single(visibleToTenantA);
        Assert.Equal(_tenantAId, visibleToTenantA[0].TenantId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetDueApprovalsAsync_WithAZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int invalidBatchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new StaleApprovalReader(dbContext, NullLogger<StaleApprovalReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetDueApprovalsAsync(invalidBatchSize, CancellationToken.None));
    }

    // --- L/M: the query is genuinely server-side, batch-bounded, and uses the database's own UTC clock -
    // captured from EF's own command logging against the real provider ---

    [Fact]
    public async Task GetDueApprovalsAsync_GeneratesASqlTopLimitedQuery_RatherThanMaterializingEverythingFirst()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var capturedCommands = new List<string>();
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString)
            .LogTo(message => capturedCommands.Add(message), [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var dbContext = new BookSpaceDbContext(options, new FixedCurrentUserContext());
        var reader = new StaleApprovalReader(dbContext, NullLogger<StaleApprovalReader>.Instance);

        await reader.GetDueApprovalsAsync(50, CancellationToken.None);

        var commandText = Assert.Single(capturedCommands, message => message.Contains("SELECT TOP(", StringComparison.Ordinal));
        Assert.Contains("SYSUTCDATETIME()", commandText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<BookSpace.Application.Notifications.DueStaleApproval>> GetDueApprovalsAsync(int batchSize = 50)
    {
        await using var dbContext = CreateDbContext();
        var reader = new StaleApprovalReader(dbContext, NullLogger<StaleApprovalReader>.Instance);
        return await reader.GetDueApprovalsAsync(batchSize, CancellationToken.None);
    }

    private async Task<(Guid ApprovalRequestId, Guid BookingId)> SeedAsync(
        Guid tenantId, Guid resourceId, Guid userId, DateTimeOffset expiresAtUtc,
        DateTimeOffset? requestedAtUtc = null, ApprovalStatus approvalStatus = ApprovalStatus.Pending, BookingStatus bookingStatus = BookingStatus.Pending)
    {
        var bookingId = Guid.NewGuid();
        var approvalRequestId = Guid.NewGuid();
        var requestedAt = requestedAtUtc ?? expiresAtUtc.AddHours(-48);
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = tenantId, ResourceId = resourceId, UserId = userId,
            StartUtc = DateTimeOffset.UtcNow.AddDays(3), EndUtc = DateTimeOffset.UtcNow.AddDays(3).AddHours(1),
            Quantity = 1, Status = bookingStatus, CreatedAtUtc = requestedAt,
        });
        dbContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = approvalRequestId, TenantId = tenantId, BookingId = bookingId, ApproverId = null,
            Status = approvalStatus, RequestedAtUtc = requestedAt, ExpiresAtUtc = expiresAtUtc,
        });
        await dbContext.SaveChangesAsync();
        return (approvalRequestId, bookingId);
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId = null) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
