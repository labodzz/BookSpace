using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves NoShowReader.GetDueNoShowsAsync's due-selection (Confirmed, CheckedInAtUtc == null, past its own
// tenant's deadline), ordering, batch-bounding, and cross-tenant read behavior against a REAL SQL Server
// (LocalDB) - see docs/background-jobs.md ("No-show job"). The "server-side filter/join, never
// materialize-then-filter," "database UTC, never client-side evaluation," and boundary-exactness claims in
// particular can only be proven here, not against SQLite or a mock.
public sealed class NoShowReaderTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNoShowReaderTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

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

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "NoShow Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now, NoShowGraceMinutes = 15 });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "NoShow Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now, NoShowGraceMinutes = 15 });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantAId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceAId, TenantId = _tenantAId, ResourceTypeId = _resourceTypeId, Name = "Room A",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceBId, TenantId = _tenantBId, ResourceTypeId = _resourceTypeId, Name = "Room B",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _userAId, TenantId = _tenantAId, FirstName = "NoShow", LastName = "A", Email = $"noshow-a-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userBId, TenantId = _tenantBId, FirstName = "NoShow", LastName = "B", Email = $"noshow-b-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- A: a Confirmed, never-checked-in booking whose deadline (StartUtc + that tenant's own
    // NoShowGraceMinutes) is already in the past is due ---

    [Fact]
    public async Task GetDueNoShowsAsync_WithAConfirmedUncheckedBookingPastItsDeadline_ReturnsIt()
    {
        // 15-minute grace, booking started 20 minutes ago -> deadline was 5 minutes ago.
        var bookingId = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));

        var result = await GetDueNoShowsAsync();

        Assert.Single(result);
        Assert.Equal(bookingId, result[0].BookingId);
        Assert.Equal(_tenantAId, result[0].TenantId);
    }

    // --- B: checked in - never a no-show candidate, however far past the deadline ---

    [Fact]
    public async Task GetDueNoShowsAsync_WithACheckedInBooking_DoesNotReturnIt()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-20), checkedInAtUtc: DateTimeOffset.UtcNow.AddMinutes(-19));

        var result = await GetDueNoShowsAsync();

        Assert.Empty(result);
    }

    // --- C: every non-Confirmed status is never a no-show candidate, even if (hypothetically) past the
    // deadline a Confirmed booking at that same StartUtc would already have ---

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.Expired)]
    public async Task GetDueNoShowsAsync_WithANonConfirmedStatus_DoesNotReturnIt(BookingStatus status)
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-20), status: status);

        var result = await GetDueNoShowsAsync();

        Assert.Empty(result);
    }

    // --- D/Boundary: just past the deadline is due. The exact "<" vs "<=" boundary instant itself cannot
    // be deterministically hit against a LIVE database clock (SYSUTCDATETIME() always advances by at least
    // a little between seeding a row and the query that reads it back, so a test seeded at "exactly now"
    // would already be milliseconds past that instant by the time the query runs, regardless of which
    // comparison operator the query uses - it would never actually exercise the "at the instant" case). The
    // true, deterministic proof that the comparison is strictly "<" (not "<=") lives in
    // NoShowProcessorTests, against NoShowProcessor's own re-check, which (like CheckInEligibilityChecker)
    // takes "now" from an injectable TimeProvider rather than a live clock - see
    // NoShowProcessorTests.ProcessDueNoShowsAsync_WithABookingExactlyAtItsDeadline_IsNotYetDue. This mirrors
    // StaleApprovalReaderTests' own precedent, which likewise only tests "clearly before"/"clearly after"
    // at the reader level and proves exact-boundary correctness at the pure-function/processor level.
    [Fact]
    public async Task GetDueNoShowsAsync_WithABookingJustPastItsDeadline_ReturnsIt()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-15).AddSeconds(-5));

        var result = await GetDueNoShowsAsync();

        Assert.Single(result);
    }

    // --- E: NOT yet due - deadline still in the future ---

    [Fact]
    public async Task GetDueNoShowsAsync_WithABookingNotYetPastItsDeadline_DoesNotReturnIt()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-5)); // only 5 of 15 minutes elapsed

        var result = await GetDueNoShowsAsync();

        Assert.Empty(result);
    }

    // --- F: a tenant-specific NoShowGraceMinutes genuinely changes which bookings are due - the deadline
    // is computed from the JOINED tenant row, never a hardcoded/global value ---

    [Fact]
    public async Task GetDueNoShowsAsync_RespectsEachTenantsOwnNoShowGraceMinutes()
    {
        await using (var dbContext = CreateDbContext())
        {
            (await dbContext.Tenants.SingleAsync(t => t.Id == _tenantBId)).NoShowGraceMinutes = 60;
            await dbContext.SaveChangesAsync();
        }

        // 20 minutes past start: past Tenant A's 15-minute grace, but well within Tenant B's 60-minute grace.
        var startUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
        var dueInTenantA = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc);
        await SeedAsync(_tenantBId, _resourceBId, _userBId, startUtc);

        var result = await GetDueNoShowsAsync();

        Assert.Single(result);
        Assert.Equal(dueInTenantA, result[0].BookingId);
    }

    // --- G: a system-wide background read must see due no-shows across EVERY tenant in one call, without
    // leaking into an ordinary tenant-scoped query ---

    [Fact]
    public async Task GetDueNoShowsAsync_ReturnsDueNoShowsFromMultipleTenantsInOneCall()
    {
        var startUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
        var a = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc);
        var b = await SeedAsync(_tenantBId, _resourceBId, _userBId, startUtc);

        var result = await GetDueNoShowsAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.BookingId == a && item.TenantId == _tenantAId);
        Assert.Contains(result, item => item.BookingId == b && item.TenantId == _tenantBId);
    }

    [Fact]
    public async Task OrdinaryTenantScopedQuery_StillOnlySeesItsOwnTenantsBookings_UnaffectedByTheNoShowReader()
    {
        var startUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc);
        await SeedAsync(_tenantBId, _resourceBId, _userBId, startUtc);

        await using var tenantScopedContext = CreateDbContext(_tenantAId);
        var visibleToTenantA = await tenantScopedContext.Bookings.ToListAsync();

        Assert.Single(visibleToTenantA);
        Assert.Equal(_tenantAId, visibleToTenantA[0].TenantId);
    }

    // --- H: bounded batch ---

    [Fact]
    public async Task GetDueNoShowsAsync_WhenMoreCandidatesExistThanBatchSize_ReturnsExactlyBatchSizeItems()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++)
        {
            await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(-20 - i), createdAtUtc: now.AddDays(-1).AddMinutes(i));
        }

        var result = await GetDueNoShowsAsync(batchSize: 5);

        Assert.Equal(5, result.Count);
    }

    // --- I: deterministic ordering - earliest deadline first, then CreatedAtUtc, then Id ---

    [Fact]
    public async Task GetDueNoShowsAsync_ReturnsCandidatesEarliestDeadlineFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var newest = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(-16));
        var oldest = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(-100));
        var middle = await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(-50));

        var result = await GetDueNoShowsAsync();

        Assert.Equal([oldest, middle, newest], result.Select(item => item.BookingId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetDueNoShowsAsync_WithAZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int invalidBatchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new NoShowReader(dbContext, NullLogger<NoShowReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetDueNoShowsAsync(invalidBatchSize, CancellationToken.None));
    }

    // --- J: the query is genuinely server-side, batch-bounded, uses the database's own UTC clock, and
    // computes the per-tenant deadline arithmetic (DATEADD) server-side too - captured from EF's own
    // command logging against the real provider, never inferred ---

    [Fact]
    public async Task GetDueNoShowsAsync_GeneratesASqlTopLimitedQueryUsingDatabaseUtcAndServerSideDeadlineArithmetic()
    {
        await SeedAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-20));

        var capturedCommands = new List<string>();
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString)
            .LogTo(message => capturedCommands.Add(message), [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var dbContext = new BookSpaceDbContext(options, new FixedCurrentUserContext());
        var reader = new NoShowReader(dbContext, NullLogger<NoShowReader>.Instance);

        await reader.GetDueNoShowsAsync(50, CancellationToken.None);

        var commandText = Assert.Single(capturedCommands, message => message.Contains("SELECT TOP(", StringComparison.Ordinal));
        Assert.Contains("SYSUTCDATETIME()", commandText, StringComparison.OrdinalIgnoreCase);
        // The per-tenant deadline (StartUtc + NoShowGraceMinutes) is computed IN SQL via DATEADD, not
        // fetched and compared in application code - this is what "no client-side evaluation" actually
        // means for this query, not just an absence of a C#-side Where() after ToListAsync().
        Assert.Contains("DATEADD", commandText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<BookSpace.Application.Notifications.DueNoShowBooking>> GetDueNoShowsAsync(int batchSize = 50)
    {
        await using var dbContext = CreateDbContext();
        var reader = new NoShowReader(dbContext, NullLogger<NoShowReader>.Instance);
        return await reader.GetDueNoShowsAsync(batchSize, CancellationToken.None);
    }

    private async Task<Guid> SeedAsync(
        Guid tenantId, Guid resourceId, Guid userId, DateTimeOffset startUtc,
        DateTimeOffset? checkedInAtUtc = null, BookingStatus status = BookingStatus.Confirmed, DateTimeOffset? createdAtUtc = null)
    {
        var bookingId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = tenantId, ResourceId = resourceId, UserId = userId,
            StartUtc = startUtc, EndUtc = startUtc.AddHours(1), Quantity = 1, Status = status,
            CheckedInAtUtc = checkedInAtUtc, CreatedAtUtc = createdAtUtc ?? startUtc.AddDays(-1),
        });
        await dbContext.SaveChangesAsync();
        return bookingId;
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
