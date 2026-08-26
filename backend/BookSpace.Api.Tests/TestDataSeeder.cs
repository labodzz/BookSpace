using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;

namespace BookSpace.Api.Tests;

// Minimal, self-contained dataset for the auth integration tests - a couple of tenants/users,
// independent from the Development-only DevelopmentSeeder (which is internal to Infrastructure
// and seeds a lot of booking data this suite doesn't need).
public static class TestDataSeeder
{
    public const string Password = "Test-Passw0rd!";

    public static readonly Guid AcmeTenantId = Guid.NewGuid();
    public static readonly Guid GlobexTenantId = Guid.NewGuid();

    public static readonly Guid AcmeAdminUserId = Guid.NewGuid();
    public const string AcmeAdminEmail = "admin@acme.integration-test";

    public static readonly Guid GlobexMemberUserId = Guid.NewGuid();
    public const string GlobexMemberEmail = "member@globex.integration-test";

    public static async Task SeedAsync(BookSpaceDbContext dbContext, IPasswordHasher passwordHasher)
    {
        var passwordHash = passwordHasher.Hash(Password);
        var now = DateTimeOffset.UtcNow;

        var tenantAdminRole = new Role { Id = Guid.NewGuid(), Name = "TenantAdmin" };
        var memberRole = new Role { Id = Guid.NewGuid(), Name = "Member" };
        dbContext.Roles.AddRange(tenantAdminRole, memberRole);

        dbContext.Tenants.AddRange(
            new Tenant { Id = AcmeTenantId, Name = "Acme Coworking", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now },
            new Tenant { Id = GlobexTenantId, Name = "Globex Labs", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });

        dbContext.Users.AddRange(
            new User { Id = AcmeAdminUserId, TenantId = AcmeTenantId, FirstName = "Acme", LastName = "Admin", Email = AcmeAdminEmail, PasswordHash = passwordHash, CreatedAtUtc = now },
            new User { Id = GlobexMemberUserId, TenantId = GlobexTenantId, FirstName = "Globex", LastName = "Member", Email = GlobexMemberEmail, PasswordHash = passwordHash, CreatedAtUtc = now });

        dbContext.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = AcmeAdminUserId, RoleId = tenantAdminRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = GlobexMemberUserId, RoleId = memberRole.Id });

        await dbContext.SaveChangesAsync();
    }
}
