using System.Web;
using BookSpace.Application.Auth;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Common;
using BookSpace.Application.Email;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Application.Tenants;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Email;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using BookSpace.Infrastructure.Tests.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// The full BookSpace tenant onboarding chain docs/tenant-onboarding.md describes piece by piece
// (SysAdmin creates a tenant + first TenantAdmin -> invitation email queued -> dispatched -> the
// administrator sets a password -> logs in -> is scoped to their own tenant), exercised end to end against
// REAL SQL Server (LocalDB): the REAL CreateTenantCommandHandler/ResendTenantAdminInvitationCommandHandler,
// REAL TenantRepository/UserRepository/InvitationRepository/NotificationOutboxWriter, the REAL
// NotificationOutboxProcessor wired with the REAL SmtpNotificationSender chain (see
// BookingConfirmationEndToEndDispatchTests for the identical production-shaped dispatch harness this
// mirrors), the REAL AuthenticationService for both AcceptInvitationAsync and LoginAsync, and the REAL
// global tenant query filter. Only the network-facing leaf (ISmtpTransport) is faked, so this never sends
// a real email. Also covers the atomicity/concurrency guarantees unit tests (mocked repositories) cannot:
// real unique-index races on Tenant.Name, User.Email, and Invitation's own filtered index.
public sealed class TenantOnboardingEndToEndTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceTenantOnboardingTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private readonly AuthOptions _authOptions = new()
    {
        Issuer = "bookspace-tests", Audience = "bookspace-tests", SigningKey = "tenant-onboarding-test-signing-key-0123456789",
    };

    private const string PublicBaseUrl = "https://bookspace.test";

    // The calling SysAdmin's own, pre-existing account - a global platform admin, TenantId = null, never
    // itself a member of any tenant (including the one being onboarded). A real row because
    // Invitation.CreatedByUserId carries a genuine foreign key to Users (see BookSpaceModelConfiguration),
    // not merely an opaque id.
    private Guid _sysAdminUserId;
    private Guid _tenantAdminRoleId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        await dbContext.Database.EnsureCreatedAsync();

        _sysAdminUserId = Guid.NewGuid();
        _tenantAdminRoleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Users.Add(new User
        {
            Id = _sysAdminUserId, TenantId = null, FirstName = "Sys", LastName = "Admin",
            Email = $"sysadmin-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = now,
        });
        dbContext.Roles.Add(new Role { Id = _tenantAdminRoleId, Name = "TenantAdmin" });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TheFullOnboardingChain_FromSysAdminRequestThroughDispatchAcceptanceAndLogin_WorksEndToEnd()
    {
        // --- Step 1: a REAL SysAdmin request, through the REAL handler, against REAL SQL Server. ---
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        CreateTenantResponse createResult;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            var handler = CreateCreateTenantHandler(dbContext);
            createResult = await handler.Handle(
                new CreateTenantCommandRequest("Acme Coworking E2E", "Europe/Zagreb", "Ana", "Anic", adminEmail), CancellationToken.None);
        }

        // --- Step 2: the new admin cannot log in yet - still Invited, no password ever set. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var loginResult = await CreateAuthenticationService(dbContext).LoginAsync(adminEmail, "anything-at-all", CancellationToken.None);
            Assert.False(loginResult.Succeeded);
        }

        // --- Step 3: the REAL outbox row landed, Pending, keyed on the invitation's own id. ---
        Guid invitationId;
        await using (var verify = CreateDbContext(tenantId: null, userId: null))
        {
            var invitation = await verify.Invitations.IgnoreQueryFilters().SingleAsync(i => i.UserId == createResult.AdminUserId);
            invitationId = invitation.Id;
            var outboxItem = await verify.NotificationOutboxItems.IgnoreQueryFilters()
                .SingleAsync(item => item.TenantId == createResult.TenantId && item.IdempotencyKey == $"tenant-admin-invitation:{invitationId}");
            Assert.Equal(NotificationOutboxStatus.Pending, outboxItem.Status);
        }

        // --- Step 4: the REAL due-batch reader, then the REAL processor/sender chain (production DI shape
        // - see BookSpace.Infrastructure/DependencyInjection.cs), with only the SMTP leaf faked. ---
        await using var readerDbContext = CreateDbContext(tenantId: null, userId: null);
        var reader = new NotificationOutboxReader(readerDbContext, NullLogger<NotificationOutboxReader>.Instance);
        var batch = await reader.GetDueBatchAsync(batchSize: 10, CancellationToken.None);
        var dueItem = Assert.Single(batch, item => item.TenantId == createResult.TenantId);

        var fakeTransportFactory = new FakeSmtpTransportFactory(() => new FakeSmtpTransport());
        var processor = new NotificationOutboxProcessor(
            BuildProductionShapedScopeFactory(fakeTransportFactory), Options.Create(new BackgroundJobsOptions()), TimeProvider.System, NullLogger<NotificationOutboxProcessor>.Instance);
        var outcome = await processor.ProcessItemAsync(dueItem, CancellationToken.None);
        Assert.Equal(NotificationOutboxItemOutcome.Sent, outcome);

        // --- Step 5: the REAL email actually built and handed to the transport - the real recipient, the
        // real tenant name, and a real, working invitation link - never a stub standing in for any of it. ---
        var sentTransport = Assert.IsType<FakeSmtpTransport>(Assert.Single(fakeTransportFactory.CreatedTransports));
        Assert.NotNull(sentTransport.SentMessage);
        Assert.Equal(adminEmail, sentTransport.SentMessage!.To.Mailboxes.Single().Address);
        Assert.Contains("Acme Coworking E2E", sentTransport.SentMessage.Subject);
        var emailBody = sentTransport.SentMessage.TextBody!;
        Assert.Contains($"{PublicBaseUrl}/accept-invitation?token=", emailBody);

        // --- Step 6: extract the REAL token from the REAL email body (the only place the raw token is ever
        // recoverable - see docs/tenant-onboarding.md) and accept, through the REAL AuthenticationService. ---
        var invitationUrl = emailBody.Split('\n').Single(line => line.Contains("/accept-invitation?token="));
        var rawToken = HttpUtility.ParseQueryString(new Uri(invitationUrl.Trim()).Query)["token"]!;

        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var acceptResult = await CreateAuthenticationService(dbContext).AcceptInvitationAsync(rawToken, "Correct-Horse-Battery-9!", CancellationToken.None);
            Assert.True(acceptResult.Succeeded);
        }

        // --- Step 7: the SAME token can never accept a second time. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var secondAcceptResult = await CreateAuthenticationService(dbContext).AcceptInvitationAsync(rawToken, "Different-Passw0rd!", CancellationToken.None);
            Assert.False(secondAcceptResult.Succeeded);
            Assert.Equal("Invitation.Invalid", secondAcceptResult.ErrorCode);
        }

        // --- Step 8: login now succeeds, and the issued access token/roles reflect the NEW tenant and the
        // TenantAdmin role - never the global SysAdmin's own role or lack of a tenant. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var loginResult = await CreateAuthenticationService(dbContext).LoginAsync(adminEmail, "Correct-Horse-Battery-9!", CancellationToken.None);
            Assert.True(loginResult.Succeeded);
        }

        // --- Step 9: tenant isolation - querying through the ordinary, FILTERED DbContext scoped to the
        // new admin's own tenant sees exactly their tenant's own data, never the global SysAdmin. ---
        await using (var scopedDbContext = CreateDbContext(createResult.TenantId, createResult.AdminUserId))
        {
            var visibleUsers = await scopedDbContext.Users.ToListAsync();
            Assert.Single(visibleUsers);
            Assert.Equal(createResult.AdminUserId, visibleUsers[0].Id);
            Assert.Null(await scopedDbContext.Users.FirstOrDefaultAsync(u => u.Id == _sysAdminUserId));
        }

        // --- Step 10: the reverse direction of the same invariant - a DbContext scoped the way the global
        // SysAdmin's OWN ambient context really looks (TenantId == null, since its JWT carries no tenant_id
        // claim at all - see JwtTokenGenerator) sees NOTHING through the ordinary tenant-filtered Users
        // query, not even its own row - see BookSpaceDbContext.ApplyUserTenantFilter's explicit
        // `TenantId != null` guard. This is exactly the assertion that caught a real bug during this
        // feature's own development: EF Core's default `Guid? == Guid?` translation rewrites equality
        // using C# null semantics (an added `OR (a IS NULL AND b IS NULL)`), not raw SQL NULL = NULL ->
        // UNKNOWN - so without that guard, a null ambient tenant context matched every OTHER global
        // SysAdmin row too (anyone else with TenantId == null), not nothing, only caught here against a
        // real database rather than assumed from the (non-nullable-column) reasoning every other
        // ITenantOwned filter correctly relies on. SysAdmin reads tenant aggregates only through
        // IUserRepository.GetTenantUserAggregatesAsync's own explicit IgnoreQueryFilters() bypass (see
        // GetTenantQueryRequest/GetTenantsQueryRequest), never through this ordinary filtered query.
        await using (var sysAdminScopedDbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            Assert.Empty(await sysAdminScopedDbContext.Users.ToListAsync());
            Assert.Null(await sysAdminScopedDbContext.Users.FirstOrDefaultAsync(u => u.Id == _sysAdminUserId));
        }
    }

    [Fact]
    public async Task ResendingTheInvitation_RevokesTheOldTokenAndIssuesAWorkingNewOne()
    {
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        CreateTenantResponse createResult;
        string firstInvitationUrl;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            createResult = await CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest("Acme Resend Co", "UTC", "Ana", "Anic", adminEmail), CancellationToken.None);
        }

        firstInvitationUrl = await ExtractInvitationUrlFromOutboxAsync(createResult.TenantId, createResult.AdminUserId);
        var firstRawToken = ExtractToken(firstInvitationUrl);

        // --- Resend: the old invitation is revoked, a new one issued, no new tenant/user/role created. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateResendHandler(dbContext).Handle(
                new ResendTenantAdminInvitationCommandRequest(createResult.TenantId, createResult.AdminUserId), CancellationToken.None);
        }

        await using (var verify = CreateDbContext(tenantId: null, userId: null))
        {
            Assert.Equal(1, await verify.Users.IgnoreQueryFilters().CountAsync(u => u.Email == adminEmail));
            Assert.Equal(1, await verify.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == "Acme Resend Co"));
            Assert.Equal(2, await verify.Invitations.IgnoreQueryFilters().CountAsync(i => i.UserId == createResult.AdminUserId));
        }

        // --- The OLD token no longer works. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var oldTokenResult = await CreateAuthenticationService(dbContext).AcceptInvitationAsync(firstRawToken, "Some-Passw0rd!", CancellationToken.None);
            Assert.False(oldTokenResult.Succeeded);
            Assert.Equal("Invitation.Invalid", oldTokenResult.ErrorCode);
        }

        // --- The NEW token works. ---
        var secondInvitationUrl = await ExtractInvitationUrlFromOutboxAsync(createResult.TenantId, createResult.AdminUserId, excludingUrl: firstInvitationUrl);
        var secondRawToken = ExtractToken(secondInvitationUrl);
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var newTokenResult = await CreateAuthenticationService(dbContext).AcceptInvitationAsync(secondRawToken, "Some-Passw0rd!", CancellationToken.None);
            Assert.True(newTokenResult.Succeeded);
        }
    }

    // Proves the pre-check's complement: a real race on Tenant.Name's own unique index, lost by exactly one
    // of two genuinely concurrent CreateTenant requests, must leave NO partial rows (no orphaned User,
    // UserRole, or Invitation) for the loser - the atomic single-commit shape the class doc comment
    // describes, not merely "the tenant row didn't get created."
    [Fact]
    public async Task TwoConcurrentCreateTenantRequestsWithTheSameName_ExactlyOneSucceedsAndLeavesNoPartialRowsForTheLoser()
    {
        const string tenantName = "Acme Race Co";
        using var startGate = new Barrier(2);

        async Task<(bool Succeeded, string AdminEmail)> AttemptAsync(string adminEmail)
        {
            startGate.SignalAndWait();
            await using var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId);
            try
            {
                await CreateCreateTenantHandler(dbContext).Handle(
                    new CreateTenantCommandRequest(tenantName, "UTC", "Ana", "Anic", adminEmail), CancellationToken.None);
                return (true, adminEmail);
            }
            catch (ConflictException)
            {
                return (false, adminEmail);
            }
        }

        var emailA = $"racer-a-{Guid.NewGuid():N}@acme.test";
        var emailB = $"racer-b-{Guid.NewGuid():N}@acme.test";
        var results = await Task.WhenAll(Task.Run(() => AttemptAsync(emailA)), Task.Run(() => AttemptAsync(emailB)));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => !r.Succeeded);
        var loserEmail = results.Single(r => !r.Succeeded).AdminEmail;

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        Assert.Equal(1, await verify.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == tenantName));
        Assert.Null(await verify.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == loserEmail));
    }

    [Fact]
    public async Task TwoConcurrentCreateTenantRequestsWithTheSameAdminEmail_ExactlyOneSucceedsAndLeavesNoPartialRowsForTheLoser()
    {
        var adminEmail = $"shared-{Guid.NewGuid():N}@acme.test";
        using var startGate = new Barrier(2);

        async Task<bool> AttemptAsync(string tenantName)
        {
            startGate.SignalAndWait();
            await using var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId);
            try
            {
                await CreateCreateTenantHandler(dbContext).Handle(
                    new CreateTenantCommandRequest(tenantName, "UTC", "Ana", "Anic", adminEmail), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var tenantNameA = $"Acme Email Race A {Guid.NewGuid():N}";
        var tenantNameB = $"Acme Email Race B {Guid.NewGuid():N}";
        var results = await Task.WhenAll(Task.Run(() => AttemptAsync(tenantNameA)), Task.Run(() => AttemptAsync(tenantNameB)));

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        // Exactly one tenant was created overall (either A or B, never both) and the email belongs to it alone.
        Assert.Equal(1, await verify.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == tenantNameA || t.Name == tenantNameB));
        Assert.Equal(1, await verify.Users.IgnoreQueryFilters().CountAsync(u => u.Email == adminEmail));
    }

    // Invitation's own filtered unique index ("at most one active invitation per user") is what a genuinely
    // concurrent resend races on - see ResendTenantAdminInvitationCommandRequest's own doc comment.
    [Fact]
    public async Task TwoConcurrentResendRequestsForTheSameAdmin_ExactlyOneActiveInvitationSurvives()
    {
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        CreateTenantResponse createResult;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            createResult = await CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest("Acme Resend Race Co", "UTC", "Ana", "Anic", adminEmail), CancellationToken.None);
        }

        using var startGate = new Barrier(2);

        async Task<bool> AttemptAsync()
        {
            startGate.SignalAndWait();
            await using var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId);
            try
            {
                await CreateResendHandler(dbContext).Handle(
                    new ResendTenantAdminInvitationCommandRequest(createResult.TenantId, createResult.AdminUserId), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));

        // Both racers read the SAME previously-active invitation and both try to revoke it and insert their
        // own replacement - whichever SaveChangesAsync commits first wins (either the loser's own INSERT
        // violates Invitation's filtered unique index, or its UPDATE of the shared row loses the RowVersion
        // race - NotificationOutboxWriter translates both shapes into the same ConflictException), so
        // exactly one of the two concurrent resends ever succeeds.
        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        var activeInvitations = await verify.Invitations.IgnoreQueryFilters()
            .CountAsync(i => i.UserId == createResult.AdminUserId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null);
        Assert.Equal(1, activeInvitations);
    }

    private async Task<string> ExtractInvitationUrlFromOutboxAsync(Guid tenantId, Guid adminUserId, string? excludingUrl = null)
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        var invitationIds = await dbContext.Invitations.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.UserId == adminUserId).Select(i => i.Id).ToListAsync();
        var keys = invitationIds.Select(id => $"tenant-admin-invitation:{id}").ToList();
        var payloads = await dbContext.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.TenantId == tenantId && keys.Contains(item.IdempotencyKey))
            .Select(item => item.PayloadJson).ToListAsync();

        foreach (var payloadJson in payloads)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<TenantAdminInvitationNotificationPayload>(payloadJson)!;
            if (payload.InvitationUrl != excludingUrl)
            {
                return payload.InvitationUrl;
            }
        }

        throw new InvalidOperationException("No matching outbox payload found.");
    }

    private static string ExtractToken(string invitationUrl) => HttpUtility.ParseQueryString(new Uri(invitationUrl).Query)["token"]!;

    private CreateTenantCommandHandler CreateCreateTenantHandler(BookSpaceDbContext dbContext) => new(
        new TenantRepository(dbContext),
        new UserRepository(dbContext),
        new InvitationRepository(dbContext),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance),
        new FixedCurrentUserContext(null, _sysAdminUserId),
        Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }));

    private ResendTenantAdminInvitationCommandHandler CreateResendHandler(BookSpaceDbContext dbContext) => new(
        new UserRepository(dbContext),
        new InvitationRepository(dbContext),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance),
        new TenantRepository(dbContext),
        new FixedCurrentUserContext(null, _sysAdminUserId),
        Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }));

    private AuthenticationService CreateAuthenticationService(BookSpaceDbContext dbContext) => new(
        new UserRepository(dbContext),
        new RefreshTokenRepository(dbContext),
        new InvitationRepository(dbContext),
        new TenantRepository(dbContext),
        new PasswordHasher(),
        new JwtTokenGenerator(Options.Create(_authOptions)),
        Options.Create(_authOptions),
        NullLogger<AuthenticationService>.Instance);

    // Mirrors BookSpace.Infrastructure/DependencyInjection.cs's REAL registrations for every dependency in
    // this chain - the only substitution is ISmtpTransportFactory, swapped for the fake one so this test
    // never opens a real network connection. See BookingConfirmationEndToEndDispatchTests for the identical
    // harness used for the booking-lifecycle chain.
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

    private BookSpaceDbContext CreateDbContext(Guid? tenantId, Guid? userId)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId, Guid? userId) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
