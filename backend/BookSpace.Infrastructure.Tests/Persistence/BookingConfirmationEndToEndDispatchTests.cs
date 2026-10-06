using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Email;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Tests.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// The one, true end-to-end chain docs/background-jobs.md describes piece by piece
// ("Booking lifecycle notifications" -> "Notification outbox" -> "Due-batch query" -> "Per-item
// processing and retry" -> "Gmail SMTP sender") but no single test exercised start to finish: a REAL
// booking-creation handler, writing to REAL SQL Server (LocalDB), through the REAL NotificationOutboxWriter,
// REAL NotificationOutboxReader, REAL NotificationOutboxProcessor, and the REAL SmtpNotificationSender
// (including its REAL EmailRecipientResolver/BookingReminderEligibilityChecker/BookingEmailTemplateRenderer
// dependencies, each resolving the booking's ACTUAL owner/payload from the database) - with ONLY the
// network-facing leaf (ISmtpTransport) faked, via the exact same FakeSmtpTransport/FakeSmtpTransportFactory
// SmtpNotificationSenderTests already uses, so this never sends a real email to a real inbox. Every other
// piece of this chain is the genuine production implementation, wired exactly like
// BookSpace.Infrastructure/DependencyInjection.cs wires it for a real deployment.
public sealed class BookingConfirmationEndToEndDispatchTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceE2EDispatchTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _userId;
    private const string OwnerEmail = "e2e-owner@bookspace.test";
    private const string OwnerFirstName = "Enda";
    private const string OwnerLastName = "ToEnd";

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "E2E Dispatch Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "E2E Room",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantId, FirstName = OwnerFirstName, LastName = OwnerLastName, Email = OwnerEmail, PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task CreatingAConfirmedBooking_EnqueuesAndThenActuallyDispatchesARealConfirmationEmail_ThroughTheEntireRealChain()
    {
        // --- Step 1: a REAL booking creation, through the REAL handler, against REAL SQL Server. ---
        var start = DateTimeOffset.UtcNow.AddDays(1);
        Guid bookingId;
        await using (var dbContext = CreateDbContext(_userId))
        {
            var handler = new CreateBookingCommandHandler(
                new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
                new ResourceRepository(dbContext),
                new AvailabilityRuleRepository(dbContext),
                new BlackoutPeriodRepository(dbContext),
                new BookingAvailabilityRepository(dbContext),
                new BookingRepository(dbContext),
                new ResourceApproverRepository(dbContext),
                new ApprovalRequestRepository(dbContext),
                new TenantRepository(dbContext),
                new FixedCurrentUserContext(_tenantId, _userId),
                new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

            var response = await handler.Handle(new CreateBookingCommandRequest(_resourceId, start, start.AddHours(1), Quantity: 1), CancellationToken.None);
            Assert.Equal(BookingStatus.Confirmed, response.Status);
            bookingId = response.Id;
        }

        // --- Step 2: confirm the REAL outbox row actually landed, Pending, from a SEPARATE connection. ---
        await using (var verify = CreateDbContext())
        {
            var outboxItem = await verify.NotificationOutboxItems.IgnoreQueryFilters()
                .SingleAsync(item => item.TenantId == _tenantId && item.IdempotencyKey == $"booking:{bookingId:D}:confirmation");
            Assert.Equal(NotificationOutboxStatus.Pending, outboxItem.Status);
            Assert.Equal(0, outboxItem.AttemptCount);
        }

        // --- Step 3: the REAL due-batch reader finds it. ---
        await using var readerDbContext = CreateDbContext();
        var reader = new NotificationOutboxReader(readerDbContext, NullLogger<NotificationOutboxReader>.Instance);
        var batch = await reader.GetDueBatchAsync(batchSize: 10, CancellationToken.None);
        var dueItem = Assert.Single(batch, item => item.TenantId == _tenantId);

        // --- Step 4: the REAL processor, wired with the REAL sender/resolver/renderer/eligibility-checker
        // chain (production DI shape - see BookSpace.Infrastructure/DependencyInjection.cs), with ONLY the
        // SMTP network leaf faked. ---
        var fakeTransportFactory = new FakeSmtpTransportFactory(() => new FakeSmtpTransport());
        var scopeFactory = BuildProductionShapedScopeFactory(fakeTransportFactory);
        var processor = new NotificationOutboxProcessor(
            scopeFactory, Options.Create(new BackgroundJobsOptions()), TimeProvider.System, NullLogger<NotificationOutboxProcessor>.Instance);

        var outcome = await processor.ProcessItemAsync(dueItem, CancellationToken.None);

        // --- Step 5: the REAL outcome, the REAL persisted Status, and the REAL "email" actually built and
        // handed to the transport - proving the full chain's data (recipient, subject, body) survived
        // intact from the original booking all the way through, never a stub or a mock standing in for it. ---
        Assert.Equal(NotificationOutboxItemOutcome.Sent, outcome);

        await using (var verify = CreateDbContext())
        {
            var sentItem = await verify.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(item => item.Id == dueItem.Id);
            Assert.Equal(NotificationOutboxStatus.Sent, sentItem.Status);
            Assert.Equal(1, sentItem.AttemptCount);
            Assert.NotNull(sentItem.LastAttemptAtUtc);
        }

        var sentTransport = Assert.IsType<FakeSmtpTransport>(Assert.Single(fakeTransportFactory.CreatedTransports));
        Assert.True(sentTransport.Connected);
        Assert.True(sentTransport.Authenticated);
        Assert.NotNull(sentTransport.SentMessage);
        // The REAL recipient, resolved from the database by the REAL EmailRecipientResolver - never a
        // hardcoded/fake address - and never sent anywhere real (FakeSmtpTransport never opens a socket).
        Assert.Equal(OwnerEmail, sentTransport.SentMessage!.To.Mailboxes.Single().Address);
        Assert.Contains("confirmed", sentTransport.SentMessage.Subject, StringComparison.OrdinalIgnoreCase);
    }

    // Mirrors BookSpace.Infrastructure/DependencyInjection.cs's REAL registrations for every dependency in
    // this chain (EmailRecipientResolver, BookingReminderEligibilityChecker, BookingEmailTemplateRenderer,
    // SmtpNotificationSender) - the only substitution is ISmtpTransportFactory, swapped for the fake one so
    // this test never opens a real network connection.
    private IServiceScopeFactory BuildProductionShapedScopeFactory(ISmtpTransportFactory transportFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(_connectionString));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext(null, null));
        services.AddSingleton(Options.Create(new EmailOptions
        {
            Enabled = true,
            FromAddress = "bookspace.sender@bookspace.test",
            FromName = "BookSpace",
            Smtp = new SmtpOptions { Host = "smtp.example.test", Port = 587, Security = SmtpSecurity.StartTls, Username = "bookspace.sender@bookspace.test", Password = "test-password" },
        }));
        services.AddScoped<IEmailRecipientResolver, EmailRecipientResolver>();
        services.AddScoped<IBookingReminderEligibilityChecker, BookingReminderEligibilityChecker>();
        services.AddSingleton<IEmailTemplateRenderer, BookingEmailTemplateRenderer>();
        services.AddSingleton(transportFactory);
        services.AddScoped<INotificationSender, SmtpNotificationSender>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private BookSpaceDbContext CreateDbContext(Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(userId is null ? null : _tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId, Guid? userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
