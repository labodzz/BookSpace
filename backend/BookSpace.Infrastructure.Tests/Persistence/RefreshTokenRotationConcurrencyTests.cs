using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
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
    private Guid _secondUserId;
    private Guid _secondTokenId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _existingTokenId = Guid.NewGuid();
        _secondUserId = Guid.NewGuid();
        _secondTokenId = Guid.NewGuid();

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
        dbContext.Users.Add(new User
        {
            Id = _secondUserId,
            TenantId = _tenantId,
            FirstName = "Second",
            LastName = "User",
            Email = $"race-2-{Guid.NewGuid():N}@bookspace.test",
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
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = _secondTokenId,
            UserId = _secondUserId,
            FamilyId = Guid.NewGuid(),
            TokenHash = $"race-hash-2-{Guid.NewGuid():N}",
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

    // Rotates `tokenId`, blocking (via `barrier`) after its own read and before its own write so a
    // caller can force two rotations of the SAME token to genuinely overlap - `Task.WhenAll` alone only
    // schedules both operations without guaranteeing their reads and writes actually interleave in time.
    // Confirmed by direct reproduction (row-level trace + EF command logging) while diagnosing this test's
    // original false pass-through: without this barrier, the second read can land 40+ ms after the
    // first task's write already committed, so it legitimately reads the POST-write RowVersion and its
    // own save then succeeds too - not a race at all, and not what this test claims to prove. `barrier`
    // is optional so single-token, non-contended callers (e.g. RotateThenRotateAgainAsync below) can
    // reuse this same helper without needing to set one up.
    private async Task<Guid?> TryRotateAsync(Guid tokenId, Guid userId, Barrier? barrier = null)
    {
        await using var dbContext = CreateDbContext();
        var existing = await dbContext.RefreshTokens.SingleAsync(token => token.Id == tokenId);

        barrier?.SignalAndWait();

        var replacement = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
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

    [Fact]
    public async Task ConcurrentRotation_OfTheSameRefreshToken_ExactlyOneSucceedsAndTheFamilyIsLeftInACleanUsableState()
    {
        // Forces both attempts to complete their read of the token (both observing the SAME original
        // RowVersion) before either is allowed to proceed to its write - this is what makes the two
        // SaveChanges calls genuinely race for the row, rather than merely running one after the other.
        using var barrier = new Barrier(2);

        var results = await Task.WhenAll(
            TryRotateAsync(_existingTokenId, _userId, barrier),
            TryRotateAsync(_existingTokenId, _userId, barrier));
        var winnerId = Assert.Single(results, id => id is not null)!.Value;

        Assert.Equal(1, results.Count(id => id is not null));
        Assert.Equal(1, results.Count(id => id is null));

        await using var verifyContext = CreateDbContext();
        var allTokens = await verifyContext.RefreshTokens.Where(token => token.UserId == _userId).ToListAsync();
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

        // Exactly one row in the family is still active (unrevoked) - not just "the winner looks fine
        // in isolation," but that the loser genuinely left no second usable token behind anywhere in
        // the family.
        var activeInFamily = allTokens.Count(token => token.FamilyId == original.FamilyId && token.RevokedAtUtc is null);
        Assert.Equal(1, activeInFamily);
    }

    [Fact]
    public async Task WinningToken_CanBeRotatedAgainAfterwards()
    {
        // A single, uncontended rotation followed by a second, uncontended rotation of the NEW token -
        // proves the winner from a race (or any rotation) is left in a state that can genuinely
        // continue the family, not just "not revoked" but actually still rotatable.
        var firstRotationId = await TryRotateAsync(_existingTokenId, _userId);
        Assert.NotNull(firstRotationId);

        var secondRotationId = await TryRotateAsync(firstRotationId!.Value, _userId);
        Assert.NotNull(secondRotationId);

        await using var verifyContext = CreateDbContext();
        var allTokens = await verifyContext.RefreshTokens.Where(token => token.UserId == _userId).ToListAsync();
        var firstReplacement = allTokens.Single(token => token.Id == firstRotationId);
        var secondReplacement = allTokens.Single(token => token.Id == secondRotationId);

        Assert.Equal(secondRotationId, firstReplacement.ReplacedByTokenId);
        Assert.NotNull(firstReplacement.RevokedAtUtc);
        Assert.Null(secondReplacement.RevokedAtUtc);
        Assert.Null(secondReplacement.ReplacedByTokenId);
    }

    [Fact]
    public async Task ConcurrentRotation_OfTwoDifferentTokensForDifferentUsers_BothSucceedIndependently()
    {
        // Two genuinely different RefreshToken rows (different users, different families) rotating at
        // the same time must not interfere with each other - the RowVersion guard is per-row, not a
        // broader lock that would serialize unrelated rotations against each other.
        var barrier = new Barrier(2);

        var results = await Task.WhenAll(
            TryRotateAsync(_existingTokenId, _userId, barrier),
            TryRotateAsync(_secondTokenId, _secondUserId, barrier));

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);

        await using var verifyContext = CreateDbContext();
        var firstOriginal = await verifyContext.RefreshTokens.SingleAsync(token => token.Id == _existingTokenId);
        var secondOriginal = await verifyContext.RefreshTokens.SingleAsync(token => token.Id == _secondTokenId);

        Assert.Equal(results[0], firstOriginal.ReplacedByTokenId);
        Assert.Equal(results[1], secondOriginal.ReplacedByTokenId);
    }

    // Verifies the physical schema, not just the EF-side `.IsRowVersion()` configuration - the database
    // column is the actual authority the optimistic-concurrency guard depends on at runtime.
    [Fact]
    public async Task RefreshTokensTable_RowVersionColumn_IsANonNullableDatabaseGeneratedRowversionColumn()
    {
        await using var dbContext = CreateDbContext();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name AS SqlTypeName, c.is_nullable, c.is_computed, c.is_identity
            FROM sys.columns c
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('RefreshTokens') AND c.name = 'RowVersion';
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "RefreshTokens.RowVersion column was not found in the physical schema.");

        // SQL Server's 'timestamp' is the storage-engine name for the exact same type EF's
        // .IsRowVersion() targets ('rowversion' is only a newer, non-deprecated alias for it).
        Assert.Equal("timestamp", reader.GetString(0));
        Assert.False(reader.GetBoolean(1), "RowVersion must be NOT NULL - a null concurrency token can never be compared.");
        // Not EF-computed and not an identity column: SQL Server itself auto-generates a fresh value on
        // every insert/update of the row, independent of anything EF sends - confirming EF never
        // supplies this value manually (also true structurally: RefreshToken.cs has no setter path for
        // it that any handler in this codebase calls).
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
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
