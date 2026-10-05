using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves BookingReminderReader.GetDueBookingsAsync's due-selection (Confirmed, within the lead-time
// window, not yet started), ordering, batch-bounding, and cross-tenant read behavior against a REAL SQL
// Server (LocalDB) - see docs/background-jobs.md ("Reminder job"). The "server-side TOP, never
// materialize-then-filter" and index-usage claims in particular can only be proven here, not against
// SQLite or a mock.
public sealed class BookingReminderReaderTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceBookingReminderReaderTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

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

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Reminder Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Reminder Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
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
        dbContext.Users.Add(new User { Id = _userAId, TenantId = _tenantAId, FirstName = "Reminder", LastName = "A", Email = $"reminder-a-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userBId, TenantId = _tenantBId, FirstName = "Reminder", LastName = "B", Email = $"reminder-b-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- A: a Confirmed booking starting within the lead time is due ---

    [Fact]
    public async Task GetDueBookingsAsync_WithAConfirmedBookingStartingWithinLeadTime_ReturnsIt()
    {
        var bookingId = await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(45), status: BookingStatus.Confirmed);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Single(result);
        Assert.Equal(bookingId, result[0].BookingId);
    }

    // --- B: too early - starts after the lead-time window - never returned ---

    [Fact]
    public async Task GetDueBookingsAsync_WithABookingStartingAfterTheLeadTimeWindow_DoesNotReturnIt()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddHours(3), status: BookingStatus.Confirmed);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Empty(result);
    }

    // --- C: already started (or starting at exactly "now") - never a due reminder ---

    [Fact]
    public async Task GetDueBookingsAsync_WithABookingThatHasAlreadyStarted_DoesNotReturnIt()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(-5), status: BookingStatus.Confirmed);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Empty(result);
    }

    // --- D: only Confirmed is ever due - Pending/Rejected/Cancelled/Completed/NoShow never are ---

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public async Task GetDueBookingsAsync_WithANonConfirmedBookingWithinTheWindow_DoesNotReturnIt(BookingStatus status)
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(30), status: status);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Empty(result);
    }

    // --- G: bounded batch - never more than batchSize, and the excluded ones are the correct (latest-
    // starting) ones, not an arbitrary subset ---

    [Fact]
    public async Task GetDueBookingsAsync_WhenMoreCandidatesExistThanBatchSize_ReturnsExactlyBatchSizeItems()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++)
        {
            await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(5 + i), status: BookingStatus.Confirmed);
        }

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60, batchSize: 5);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task GetDueBookingsAsync_WhenMoreCandidatesExistThanBatchSize_ExcludesTheLatestStartingOnesNotJustAnyExtras()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            // Soonest (i=0) to latest (i=6) starting, all still inside the window.
            ids.Add(await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(5 + i), status: BookingStatus.Confirmed));
        }

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60, batchSize: 5);

        Assert.Equal(ids.Take(5), result.Select(item => item.BookingId));
    }

    [Fact]
    public async Task GetDueBookingsAsync_ReturnsCandidatesSoonestStartingFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var later = await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(50), status: BookingStatus.Confirmed);
        var soonest = await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(10), status: BookingStatus.Confirmed);
        var middle = await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: now.AddMinutes(30), status: BookingStatus.Confirmed);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Equal([soonest, middle, later], result.Select(item => item.BookingId));
    }

    // --- H: a system-wide background read must see due bookings across EVERY tenant in one call ---

    [Fact]
    public async Task GetDueBookingsAsync_ReturnsDueBookingsFromMultipleTenantsInOneCall()
    {
        var bookingA = await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);
        var bookingB = await SeedBookingAsync(_tenantBId, _resourceBId, _userBId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);

        var result = await GetDueBookingsAsync(leadTimeMinutes: 60);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.BookingId == bookingA && item.TenantId == _tenantAId);
        Assert.Contains(result, item => item.BookingId == bookingB && item.TenantId == _tenantBId);
    }

    // --- The other half of the tenant requirement: an ordinary, tenant-scoped query is unaffected by this
    // reader's IgnoreQueryFilters() existing ---

    [Fact]
    public async Task OrdinaryTenantScopedQuery_StillOnlySeesItsOwnTenantsBookings_UnaffectedByTheReminderReader()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);
        await SeedBookingAsync(_tenantBId, _resourceBId, _userBId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);

        await using var tenantScopedContext = CreateDbContext(_tenantAId);
        var visibleToTenantA = await tenantScopedContext.Bookings.ToListAsync();

        Assert.Single(visibleToTenantA);
        Assert.Equal(_tenantAId, visibleToTenantA[0].TenantId);
    }

    [Fact]
    public async Task GetDueBookingsAsync_WithAnAlreadyCancelledToken_ThrowsWithoutReturning()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);

        await using var dbContext = CreateDbContext();
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.GetDueBookingsAsync(TimeSpan.FromMinutes(60), 50, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetDueBookingsAsync_WithAZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int invalidBatchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetDueBookingsAsync(TimeSpan.FromMinutes(60), invalidBatchSize, CancellationToken.None));
    }

    [Fact]
    public async Task GetDueBookingsAsync_WithAZeroOrNegativeLeadTime_ThrowsArgumentOutOfRangeException()
    {
        await using var dbContext = CreateDbContext();
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetDueBookingsAsync(TimeSpan.Zero, 50, CancellationToken.None));
    }

    // --- I/J: the query is genuinely server-side and batch-bounded in the SQL itself (SELECT TOP(@p)),
    // not "fetch everything, then Take/Where in memory" - captured from EF's own command logging against
    // the real provider ---

    [Fact]
    public async Task GetDueBookingsAsync_GeneratesASqlTopLimitedQuery_RatherThanMaterializingEverythingFirst()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);

        var capturedCommands = new List<string>();
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString)
            .LogTo(message => capturedCommands.Add(message), [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var dbContext = new BookSpaceDbContext(options, new FixedCurrentUserContext());
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        await reader.GetDueBookingsAsync(TimeSpan.FromMinutes(60), 50, CancellationToken.None);

        Assert.Contains(capturedCommands, message => message.Contains("SELECT TOP(", StringComparison.Ordinal));
    }

    // Proves both range bounds (the "already past" lower bound and the "not too far in the future" upper
    // bound) are evaluated by the DATABASE's own clock, never materialized from this application
    // instance's clock first - see docs/background-jobs.md ("Reminder job - database clock"). A query
    // string containing a literal DECLARE/SELECT parameter value for "now" (rather than a SYSUTCDATETIME()
    // call) would mean the app's own DateTimeOffset.UtcNow was captured and sent as a parameter instead.
    [Fact]
    public async Task GetDueBookingsAsync_UsesTheServersOwnClockForBothRangeBounds_NeverTheApplicationsClock()
    {
        await SeedBookingAsync(_tenantAId, _resourceAId, _userAId, startUtc: DateTimeOffset.UtcNow.AddMinutes(10), status: BookingStatus.Confirmed);

        var capturedCommands = new List<string>();
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString)
            .LogTo(message => capturedCommands.Add(message), [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var dbContext = new BookSpaceDbContext(options, new FixedCurrentUserContext());
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);

        await reader.GetDueBookingsAsync(TimeSpan.FromMinutes(60), 50, CancellationToken.None);

        var commandText = capturedCommands.Single(message => message.Contains("SELECT TOP(", StringComparison.Ordinal));
        // SYSUTCDATETIME() appearing at all - rather than a literal datetime value baked in as a query
        // parameter - is what proves both the lower bound (already past) and the upper bound (not too far
        // in the future, via DATEADD(minute, @leadTimeMinutes, SYSUTCDATETIME())) are evaluated by the
        // database's own clock at execution time, never captured from this application instance's clock
        // first. Appears (at least) twice: once per bound.
        var occurrences = commandText.Split("SYSUTCDATETIME()", StringSplitOptions.None).Length - 1;
        Assert.True(occurrences >= 2, $"Expected SYSUTCDATETIME() to appear at least twice in the generated SQL, found {occurrences}. SQL: {commandText}");
    }

    private async Task<IReadOnlyList<BookSpace.Application.Notifications.DueBookingReminder>> GetDueBookingsAsync(int leadTimeMinutes, int batchSize = 50)
    {
        await using var dbContext = CreateDbContext();
        var reader = new BookingReminderReader(dbContext, NullLogger<BookingReminderReader>.Instance);
        return await reader.GetDueBookingsAsync(TimeSpan.FromMinutes(leadTimeMinutes), batchSize, CancellationToken.None);
    }

    private async Task<Guid> SeedBookingAsync(Guid tenantId, Guid resourceId, Guid userId, DateTimeOffset startUtc, BookingStatus status)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = id,
            TenantId = tenantId,
            ResourceId = resourceId,
            UserId = userId,
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(1),
            Quantity = 1,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    // Default TenantId => null, mirroring the system-wide background context GetDueBookingsAsync actually
    // runs under - see docs/tenant-isolation.md.
    private sealed class FixedCurrentUserContext(Guid? tenantId = null) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
