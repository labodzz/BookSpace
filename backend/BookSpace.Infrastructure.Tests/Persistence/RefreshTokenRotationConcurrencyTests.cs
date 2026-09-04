using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Exercises the RowVersion-backed optimistic concurrency guard on RefreshToken against a REAL SQL
// Server (LocalDB) - a RowVersion mismatch surfaces as DbUpdateConcurrencyException, which only a real
// database enforces (there's nothing to simulate against mocks). Mirrors
// SaveChangesHandlingConflictsAsyncTests's throwaway-database pattern.
public sealed class RefreshTokenRotationConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceRefreshRaceTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _userId;
    private Guid _existingTokenId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _existingTokenId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Name = "Refresh Race Tenant",
            DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.Users.Add(new User
        {
            Id = _userId,
            TenantId = _tenantId,
            FirstName = "Race",
            LastName = "User",
            Email = $"race-{Guid.NewGuid():N}@bookspace.test",
            PasswordHash = "irrelevant-for-this-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = _existingTokenId,
            UserId = _userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = $"race-hash-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(13),
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ConcurrentRotation_OfTheSameRefreshToken_ExactlyOneSucceedsAndTheFamilyIsLeftInACleanUsableState()
    {
        async Task<Guid?> TryRotateAsync()
        {
            await using var dbContext = CreateDbContext();
            var existing = await dbContext.RefreshTokens.SingleAsync(token => token.Id == _existingTokenId);

            var replacement = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                FamilyId = existing.FamilyId,
                TokenHash = $"rotated-hash-{Guid.NewGuid():N}",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(14),
            };
            await dbContext.RefreshTokens.AddAsync(replacement);

            existing.ReplacedByTokenId = replacement.Id;
            existing.RevokedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await dbContext.SaveChangesHandlingConflictsAsync(CancellationToken.None);
                return replacement.Id;
            }
            catch (ConflictException)
            {
                return null;
            }
        }

        var results = await Task.WhenAll(TryRotateAsync(), TryRotateAsync());
        var winnerId = Assert.Single(results, id => id is not null)!.Value;

        Assert.Equal(1, results.Count(id => id is not null));
        Assert.Equal(1, results.Count(id => id is null));

        await using var verifyContext = CreateDbContext();
        var allTokens = await verifyContext.RefreshTokens.ToListAsync();
        var original = allTokens.Single(token => token.Id == _existingTokenId);
        var winner = allTokens.Single(token => token.Id == winnerId);

        // Exactly the original plus one child exist - the loser's insert was rolled back with its
        // failed update, not left as an orphan alongside the winner's (proves atomicity of the rollback,
        // not just "one attempt returned false").
        Assert.Equal(2, allTokens.Count);
        Assert.Equal(winnerId, original.ReplacedByTokenId);
        Assert.NotNull(original.RevokedAtUtc);

        // The full family security invariant, not just "one attempt succeeded": the WINNER's brand-new
        // token must remain live and usable. A losing concurrent request is a race, not a reuse/theft
        // signal (see the comment on AuthenticationService.RefreshAsync's ConflictException catch) - it
        // must never revoke the family and take down the request that actually won.
        Assert.Null(winner.RevokedAtUtc);
        Assert.Null(winner.ReplacedByTokenId);
    }

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
