using System.Security.Cryptography;
using System.Text;
using BookSpace.Application.Auth;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves "prevent the token from being reused concurrently" against a REAL SQL Server (LocalDB) -
// Invitation.RowVersion is a genuine optimistic-concurrency guard only a real database enforces (SQLite,
// used elsewhere in this suite for speed, has no rowversion support and the column is a no-op there).
// Exercises the real AuthenticationService.AcceptInvitationAsync production code path, not a
// reimplementation, backed by real UserRepository/InvitationRepository/PasswordHasher instances.
public sealed class AcceptInvitationConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceAcceptInvitationRaceTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private readonly AuthOptions _authOptions = new()
    {
        Issuer = "bookspace-tests", Audience = "bookspace-tests", SigningKey = "concurrency-test-signing-key-0123456789",
    };

    private Guid _tenantId;
    private Guid _userId;
    private Guid _invitationId;
    private const string RawToken = "a-fixed-raw-invitation-token-for-this-race";

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _invitationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Accept Race Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User
        {
            Id = _userId, TenantId = _tenantId, FirstName = "Race", LastName = "Invitee",
            Email = $"race-invitee-{Guid.NewGuid():N}@bookspace.test", PasswordHash = string.Empty, Status = UserStatus.Invited, CreatedAtUtc = now,
        });
        dbContext.Invitations.Add(new Invitation
        {
            Id = _invitationId, TenantId = _tenantId, UserId = _userId, CreatedByUserId = _userId,
            TokenHash = HashToken(RawToken), CreatedAtUtc = now, ExpiresAtUtc = now.AddHours(72),
        });
        await dbContext.SaveChangesAsync();
    }

    // Mirrors SecureTokenGenerator.HashToken exactly (internal to BookSpace.Application, not visible to
    // this test project) - the real AuthenticationService.AcceptInvitationAsync hashes the presented raw
    // token the same way before looking it up, so the seeded row here must be keyed by the same hash.
    private static string HashToken(string rawToken) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TwoConcurrentAcceptancesOfTheSameToken_ExactlyOneSucceedsAndSetsThePassword()
    {
        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> TryAcceptAsync(string password)
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext();
            var sut = CreateAuthenticationService(dbContext);
            var result = await sut.AcceptInvitationAsync(RawToken, password, CancellationToken.None);
            return result.Succeeded;
        }

        var taskA = Task.Run(() => TryAcceptAsync("First-Passw0rd!"));
        var taskB = Task.Run(() => TryAcceptAsync("Second-Passw0rd!"));
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext();
        var user = await verifyContext.Users.SingleAsync(u => u.Id == _userId);
        var invitation = await verifyContext.Invitations.SingleAsync(i => i.Id == _invitationId);

        Assert.Equal(UserStatus.Active, user.Status);
        Assert.NotNull(invitation.AcceptedAtUtc);
        // Exactly one of the two candidate passwords is the one that actually got set - never a mix, and
        // never left at the empty placeholder hash (which would mean neither write actually landed).
        var hasher = new PasswordHasher();
        var winnerIsFirst = hasher.Verify(user.PasswordHash, "First-Passw0rd!");
        var winnerIsSecond = hasher.Verify(user.PasswordHash, "Second-Passw0rd!");
        Assert.True(winnerIsFirst ^ winnerIsSecond);
    }

    [Fact]
    public async Task AcceptingAnAlreadyAcceptedInvitation_FailsAndLeavesTheOriginalPasswordInPlace()
    {
        await using var firstAttemptContext = CreateDbContext();
        var firstResult = await CreateAuthenticationService(firstAttemptContext).AcceptInvitationAsync(RawToken, "Original-Passw0rd!", CancellationToken.None);
        Assert.True(firstResult.Succeeded);

        await using var secondAttemptContext = CreateDbContext();
        var secondResult = await CreateAuthenticationService(secondAttemptContext).AcceptInvitationAsync(RawToken, "Different-Passw0rd!", CancellationToken.None);

        Assert.False(secondResult.Succeeded);
        Assert.Equal("Invitation.Invalid", secondResult.ErrorCode);

        await using var verifyContext = CreateDbContext();
        var user = await verifyContext.Users.SingleAsync(u => u.Id == _userId);
        Assert.True(new PasswordHasher().Verify(user.PasswordHash, "Original-Passw0rd!"));
    }

    private AuthenticationService CreateAuthenticationService(BookSpaceDbContext dbContext) => new(
        new UserRepository(dbContext),
        new RefreshTokenRepository(dbContext),
        new InvitationRepository(dbContext),
        new PasswordHasher(),
        new JwtTokenGenerator(Options.Create(_authOptions)),
        Options.Create(_authOptions),
        NullLogger<AuthenticationService>.Instance);

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(_tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
