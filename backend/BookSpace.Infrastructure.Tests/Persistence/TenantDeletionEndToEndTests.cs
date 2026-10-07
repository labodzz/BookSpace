using System.Security.Cryptography;
using System.Text;
using BookSpace.Application.Auth;
using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Application.Tenants;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// The soft-delete-and-release-for-reuse chain docs/tenant-onboarding.md describes ("Tenant lifecycle"),
// exercised end to end against REAL SQL Server (LocalDB): the REAL DeleteTenantCommandHandler cascading
// through the REAL UserRepository/InvitationRepository/CalendarSubscriptionRepository/RefreshTokenRepository,
// the REAL filtered unique indexes actually enforcing (or releasing) Tenant.Name/User.Email, the REAL
// AuthenticationService for Login/Refresh/AcceptInvitation, and the REAL DeletedTenantAccessGuard - the one
// live-session check nothing else in this suite exercises against a real database. Mirrors the harness
// TenantOnboardingEndToEndTests already established for the create/invite/accept chain.
public sealed class TenantDeletionEndToEndTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceTenantDeletionTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private readonly AuthOptions _authOptions = new()
    {
        Issuer = "bookspace-tests", Audience = "bookspace-tests", SigningKey = "tenant-deletion-test-signing-key-0123456789",
    };

    private const string PublicBaseUrl = "https://bookspace.test";

    private Guid _sysAdminUserId;
    private string _sysAdminEmail = null!;
    private Guid _tenantAdminRoleId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        await dbContext.Database.EnsureCreatedAsync();

        _sysAdminUserId = Guid.NewGuid();
        _sysAdminEmail = $"sysadmin-{Guid.NewGuid():N}@bookspace.test";
        _tenantAdminRoleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Users.Add(new User
        {
            Id = _sysAdminUserId, TenantId = null, FirstName = "Sys", LastName = "Admin",
            Email = _sysAdminEmail, PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = now,
        });
        dbContext.Roles.Add(new Role { Id = _tenantAdminRoleId, Name = "TenantAdmin" });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Builds a tenant with an ACTIVE admin (password set, logged in at least once - a real refresh token
    // and a real calendar subscription on file) plus a SECOND, still-Invited user with their own active
    // invitation - the fullest realistic "live" shape a tenant can have before it is deleted.
    private async Task<(Tenant Tenant, User Admin, string AdminPassword, AuthTokens AdminTokens, CalendarSubscription Subscription,
            User InvitedUser, Invitation PendingInvitation, string PendingInvitationRawToken)>
        SeedALiveTenantAsync(string tenantName)
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@acme.test";
        CreateTenantResponse createResult;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            createResult = await CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest(tenantName, "UTC", "Ana", "Anic", adminEmail), CancellationToken.None);
        }

        const string adminPassword = "Correct-Horse-Battery-9!";
        var invitationUrl = await ExtractInvitationUrlFromOutboxAsync(createResult.TenantId, createResult.AdminUserId);
        var rawToken = ExtractToken(invitationUrl);

        AuthTokens adminTokens;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var acceptResult = await CreateAuthenticationService(dbContext).AcceptInvitationAsync(rawToken, adminPassword, CancellationToken.None);
            Assert.True(acceptResult.Succeeded);
        }

        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var loginResult = await CreateAuthenticationService(dbContext).LoginAsync(adminEmail, adminPassword, CancellationToken.None);
            Assert.True(loginResult.Succeeded);
            adminTokens = loginResult.Tokens!;
        }

        CalendarSubscription subscription;
        await using (var dbContext = CreateDbContext(createResult.TenantId, createResult.AdminUserId))
        {
            subscription = new CalendarSubscription
            {
                Id = Guid.NewGuid(), TenantId = createResult.TenantId, UserId = createResult.AdminUserId,
                TokenHash = $"hash-{Guid.NewGuid()}", CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            dbContext.CalendarSubscriptions.Add(subscription);
            await dbContext.SaveChangesAsync();
        }

        User invitedUser;
        Invitation pendingInvitation;
        // A real, known raw token hashed the same way AuthenticationService.AcceptInvitationAsync hashes
        // a presented one (see HashToken's own doc comment) - SecureTokenGenerator itself is internal to
        // BookSpace.Application and not visible here, the same reason AcceptInvitationConcurrencyTests
        // reimplements it locally rather than referencing it.
        var pendingInvitationRawToken = $"raw-token-{Guid.NewGuid():N}";
        await using (var dbContext = CreateDbContext(createResult.TenantId, createResult.AdminUserId))
        {
            invitedUser = new User
            {
                Id = Guid.NewGuid(), TenantId = createResult.TenantId, FirstName = "Ivo", LastName = "Ivic",
                Email = $"ivo-{Guid.NewGuid():N}@acme.test", PasswordHash = string.Empty, Status = UserStatus.Invited, CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            dbContext.Users.Add(invitedUser);
            pendingInvitation = new Invitation
            {
                Id = Guid.NewGuid(), TenantId = createResult.TenantId, UserId = invitedUser.Id, TokenHash = HashToken(pendingInvitationRawToken),
                CreatedByUserId = createResult.AdminUserId, CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(72),
            };
            dbContext.Invitations.Add(pendingInvitation);
            await dbContext.SaveChangesAsync();
        }

        await using (var verify = CreateDbContext(tenantId: null, userId: null))
        {
            var admin = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == createResult.AdminUserId);
            var tenant = await verify.Tenants.SingleAsync(t => t.Id == createResult.TenantId);
            return (tenant, admin, adminPassword, adminTokens, subscription, invitedUser, pendingInvitation, pendingInvitationRawToken);
        }
    }

    // Mirrors SecureTokenGenerator.HashToken exactly (internal to BookSpace.Application, not visible to
    // this test project) - see AcceptInvitationConcurrencyTests for the identical, pre-existing pattern.
    private static string HashToken(string rawToken) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    [Fact]
    public async Task DeletingATenant_CutsOffEveryFormOfLiveAccessForItsUsers()
    {
        var (tenant, admin, adminPassword, adminTokens, subscription, invitedUser, pendingInvitation, pendingInvitationRawToken) =
            await SeedALiveTenantAsync($"Acme Full Lifecycle Co {Guid.NewGuid():N}");

        // --- Before delete: everything still works. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            Assert.True((await CreateAuthenticationService(dbContext).LoginAsync(admin.Email, adminPassword, CancellationToken.None)).Succeeded);
            Assert.False(await CreateDeletedTenantAccessGuard(dbContext).IsRevokedAsync(admin.Id, CancellationToken.None));
        }

        // --- The delete itself. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            var result = await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
            Assert.Equal(TenantStatus.Deleted, result.Status);
        }

        // --- Login: the old password no longer gets anyone in. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var loginResult = await CreateAuthenticationService(dbContext).LoginAsync(admin.Email, adminPassword, CancellationToken.None);
            Assert.False(loginResult.Succeeded);
        }

        // --- Refresh: the admin's pre-delete refresh token is dead. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var refreshResult = await CreateAuthenticationService(dbContext).RefreshAsync(adminTokens.RefreshToken, CancellationToken.None);
            Assert.False(refreshResult.Succeeded);
        }

        // --- Accept-invitation: the second, still-pending user's invite link is dead too. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var acceptResult = await CreateAuthenticationService(dbContext)
                .AcceptInvitationAsync(pendingInvitationRawToken, "Some-Passw0rd!", CancellationToken.None);
            Assert.False(acceptResult.Succeeded);
        }

        // --- Calendar feed: the admin's subscription token no longer resolves. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            var repository = new CalendarSubscriptionRepository(dbContext);
            Assert.Null(await repository.FindActiveByTokenHashAsync(subscription.TokenHash, CancellationToken.None));
        }

        // --- The core new guarantee: a still-unexpired access token must ALSO stop working for ordinary
        // API calls, not just the next refresh/login - see IDeletedTenantAccessGuard's own doc comment. ---
        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            Assert.True(await CreateDeletedTenantAccessGuard(dbContext).IsRevokedAsync(admin.Id, CancellationToken.None));
            Assert.True(await CreateDeletedTenantAccessGuard(dbContext).IsRevokedAsync(invitedUser.Id, CancellationToken.None));
        }

        // --- Pending invitation row itself is durably revoked, not merely "ignored". ---
        await using (var verify = CreateDbContext(tenantId: null, userId: null))
        {
            var invitation = await verify.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Id == pendingInvitation.Id);
            Assert.NotNull(invitation.RevokedAtUtc);
        }
    }

    [Fact]
    public async Task DeletingATenant_ReleasesItsNameForABrandNewTenant()
    {
        var tenantName = $"Acme Reusable Name Co {Guid.NewGuid():N}";
        var (tenant, _, _, _, _, _, _, _) = await SeedALiveTenantAsync(tenantName);

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenantName), CancellationToken.None);
        }

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            var newTenantResult = await CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest(tenantName, "UTC", "New", "Owner", $"new-owner-{Guid.NewGuid():N}@acme.test"), CancellationToken.None);
            Assert.NotEqual(tenant.Id, newTenantResult.TenantId);
            Assert.Equal(tenantName, newTenantResult.TenantName);
        }

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        Assert.Equal(2, await verify.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == tenantName));
    }

    [Fact]
    public async Task DeletingATenant_ReleasesItsAdminsEmailForABrandNewTenantsAdmin_WithABrandNewUserId()
    {
        var (tenant, admin, _, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme Reusable Email Co {Guid.NewGuid():N}");
        var oldAdminId = admin.Id;
        var reusedEmail = admin.Email;

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
        }

        CreateTenantResponse newTenantResult;
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            newTenantResult = await CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest($"Acme New Owner Co {Guid.NewGuid():N}", "UTC", "New", "Owner", reusedEmail), CancellationToken.None);
        }

        // A brand-new row, a brand-new id - never resurrects or reattaches to the withdrawn one.
        Assert.NotEqual(oldAdminId, newTenantResult.AdminUserId);

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        var rows = await verify.Users.IgnoreQueryFilters().Where(u => u.Email == reusedEmail).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, u => u.Id == oldAdminId && u.Status == UserStatus.Withdrawn);
        Assert.Contains(rows, u => u.Id == newTenantResult.AdminUserId && u.Status == UserStatus.Invited);

        // Login now resolves to the NEW account, never the withdrawn one - proven through the real,
        // filtered FindByEmailAsync-backed login path, not merely the raw row count above.
        await using (var acceptContext = CreateDbContext(tenantId: null, userId: null))
        {
            var invitationUrl = await ExtractInvitationUrlFromOutboxAsync(newTenantResult.TenantId, newTenantResult.AdminUserId);
            var acceptResult = await CreateAuthenticationService(acceptContext)
                .AcceptInvitationAsync(ExtractToken(invitationUrl), "Brand-New-Passw0rd!", CancellationToken.None);
            Assert.True(acceptResult.Succeeded);
        }

        await using (var loginContext = CreateDbContext(tenantId: null, userId: null))
        {
            var loginResult = await CreateAuthenticationService(loginContext).LoginAsync(reusedEmail, "Brand-New-Passw0rd!", CancellationToken.None);
            Assert.True(loginResult.Succeeded);
        }
    }

    [Fact]
    public async Task DeletingATenant_NeverAffectsAnotherTenantsUsersOrLiveAccess()
    {
        var (deletedTenant, _, _, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme Deleted Co {Guid.NewGuid():N}");
        var (survivingTenant, survivingAdmin, survivingPassword, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme Survivor Co {Guid.NewGuid():N}");

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(deletedTenant.Id, deletedTenant.Name), CancellationToken.None);
        }

        await using (var dbContext = CreateDbContext(tenantId: null, userId: null))
        {
            Assert.True((await CreateAuthenticationService(dbContext).LoginAsync(survivingAdmin.Email, survivingPassword, CancellationToken.None)).Succeeded);
            Assert.False(await CreateDeletedTenantAccessGuard(dbContext).IsRevokedAsync(survivingAdmin.Id, CancellationToken.None));
        }

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        var survivingTenantAfter = await verify.Tenants.SingleAsync(t => t.Id == survivingTenant.Id);
        Assert.Equal(TenantStatus.Active, survivingTenantAfter.Status);
    }

    [Fact]
    public async Task DeletingATenant_PreservesBookingHistoryUnderTheOriginalTenantAndUserIds()
    {
        var (tenant, admin, _, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme History Co {Guid.NewGuid():N}");

        var resourceTypeId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(-3);
        await using (var dbContext = CreateDbContext(tenant.Id, admin.Id))
        {
            dbContext.ResourceTypes.Add(new ResourceType { Id = resourceTypeId, TenantId = tenant.Id, Name = "Desk" });
            dbContext.Resources.Add(new Resource
            {
                Id = resourceId, TenantId = tenant.Id, ResourceTypeId = resourceTypeId, Name = "Desk 1",
                Capacity = 1, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
            });
            dbContext.Bookings.Add(new Booking
            {
                Id = bookingId, TenantId = tenant.Id, ResourceId = resourceId, UserId = admin.Id,
                StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start,
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
        }

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        var booking = await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(tenant.Id, booking.TenantId);
        Assert.Equal(admin.Id, booking.UserId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
    }

    [Fact]
    public async Task DeletingAnAlreadyDeletedTenant_ThrowsConflictAndMutatesNothingFurther()
    {
        var (tenant, admin, _, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme Repeat Delete Co {Guid.NewGuid():N}");

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
        }

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            var exception = await Assert.ThrowsAsync<ConflictException>(
                () => CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None));
            Assert.Equal("Tenant.StatusConflict", exception.ErrorCode);
        }

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);
        Assert.Equal(UserStatus.Withdrawn, user.Status); // unchanged by the rejected second attempt
    }

    // No RowVersion on Tenant/User themselves, but Invitation does carry one (see
    // BookSpaceModelConfiguration) - two genuinely concurrent deletes of the SAME tenant both try to revoke
    // the SAME active invitation row, so exactly one of their two SaveChangesAsync calls wins; the loser's
    // ENTIRE cascade (tenant status, every withdrawn user, every revoked row) rolls back together, not just
    // the invitation write that actually collided - proving the single-commit atomicity the handler's own
    // doc comment describes, under real concurrency.
    [Fact]
    public async Task TwoConcurrentDeleteRequestsForTheSameTenant_ExactlyOneSucceedsAndTheFinalStateIsFullyDeleted()
    {
        var (tenant, admin, _, _, _, invitedUser, _, _) = await SeedALiveTenantAsync($"Acme Concurrent Delete Co {Guid.NewGuid():N}");
        using var startGate = new Barrier(2);

        async Task<bool> AttemptAsync()
        {
            startGate.SignalAndWait();
            await using var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId);
            try
            {
                await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verify = CreateDbContext(tenantId: null, userId: null);
        Assert.Equal(TenantStatus.Deleted, (await verify.Tenants.SingleAsync(t => t.Id == tenant.Id)).Status);
        Assert.Equal(UserStatus.Withdrawn, (await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id)).Status);
        Assert.Equal(UserStatus.Withdrawn, (await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == invitedUser.Id)).Status);
    }

    [Fact]
    public async Task DeletingATenant_NeverWithdrawsTheGlobalSysAdminAndNeverReleasesItsEmail()
    {
        var (tenant, _, _, _, _, _, _, _) = await SeedALiveTenantAsync($"Acme SysAdmin Untouched Co {Guid.NewGuid():N}");

        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            await CreateDeleteHandler(dbContext).Handle(new DeleteTenantCommandRequest(tenant.Id, tenant.Name), CancellationToken.None);
        }

        await using (var verify = CreateDbContext(tenantId: null, userId: null))
        {
            var sysAdmin = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _sysAdminUserId);
            Assert.Equal(UserStatus.Active, sysAdmin.Status);
        }

        // The SysAdmin's own email still blocks a brand-new account - this task's Section 2 requirement
        // that a global platform account is never subject to a tenant's own lifecycle.
        await using (var dbContext = CreateDbContext(tenantId: null, userId: _sysAdminUserId))
        {
            var exception = await Assert.ThrowsAsync<ConflictException>(() => CreateCreateTenantHandler(dbContext).Handle(
                new CreateTenantCommandRequest($"Acme Impersonation Co {Guid.NewGuid():N}", "UTC", "Someone", "Else", _sysAdminEmail), CancellationToken.None));
            Assert.Equal("User.EmailConflict", exception.ErrorCode);
        }
    }

    private async Task<string> ExtractInvitationUrlFromOutboxAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateDbContext(tenantId: null, userId: null);
        var invitationIds = await dbContext.Invitations.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.UserId == userId).Select(i => i.Id).ToListAsync();
        var keys = invitationIds.Select(id => $"tenant-admin-invitation:{id}").ToList();
        var payloadJson = await dbContext.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.TenantId == tenantId && keys.Contains(item.IdempotencyKey))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => item.PayloadJson)
            .FirstAsync();
        return System.Text.Json.JsonSerializer.Deserialize<TenantAdminInvitationNotificationPayload>(payloadJson)!.InvitationUrl;
    }

    private static string ExtractToken(string invitationUrl) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(invitationUrl).Query)["token"]!;

    private CreateTenantCommandHandler CreateCreateTenantHandler(BookSpaceDbContext dbContext) => new(
        new TenantRepository(dbContext),
        new UserRepository(dbContext),
        new InvitationRepository(dbContext),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance),
        new FixedCurrentUserContext(null, _sysAdminUserId),
        Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl }));

    private DeleteTenantCommandHandler CreateDeleteHandler(BookSpaceDbContext dbContext) => new(
        new TenantRepository(dbContext),
        new UserRepository(dbContext),
        new InvitationRepository(dbContext),
        new CalendarSubscriptionRepository(dbContext),
        new RefreshTokenRepository(dbContext));

    private AuthenticationService CreateAuthenticationService(BookSpaceDbContext dbContext) => new(
        new UserRepository(dbContext),
        new RefreshTokenRepository(dbContext),
        new InvitationRepository(dbContext),
        new TenantRepository(dbContext),
        new PasswordHasher(),
        new JwtTokenGenerator(Options.Create(_authOptions)),
        Options.Create(_authOptions),
        NullLogger<AuthenticationService>.Instance);

    private BookSpace.Application.Auth.DeletedTenantAccessGuard CreateDeletedTenantAccessGuard(BookSpaceDbContext dbContext) => new(
        new UserRepository(dbContext), new TenantRepository(dbContext));

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
