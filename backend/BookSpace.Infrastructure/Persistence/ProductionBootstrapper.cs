using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Creates the first global SysAdmin in an otherwise-empty production database - the production
// counterpart to DevelopmentSeeder's own sysadmin@bookspace.test account, gated by
// BootstrapOptions.Enabled instead of the Development environment check. Called from Program.cs only
// after migrations have already run (see DependencyInjection.BootstrapProductionAdminAsync), never in
// place of them and never alongside DevelopmentSeeder - the two are mutually exclusive by construction
// (one runs only in Development, this one only when explicitly enabled by configuration).
//
// Deliberately does NOT create a tenant: a platform with zero tenants is a normal, expected state
// immediately after bootstrap - the SysAdmin creates the first tenant through the ordinary create-tenant
// API afterward (see CreateTenantCommandHandler), the same path every later tenant goes through. See
// BootstrapOptions for why this replaced an earlier version that bootstrapped a tenant + TenantAdmin.
internal static class ProductionBootstrapper
{
    private const string SysAdminRoleName = "SysAdmin";

    public static async Task SeedAsync(
        BookSpaceDbContext dbContext,
        IPasswordHasher passwordHasher,
        BootstrapOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SysAdminFirstName)
            || string.IsNullOrWhiteSpace(options.SysAdminLastName)
            || string.IsNullOrWhiteSpace(options.SysAdminEmail)
            || string.IsNullOrWhiteSpace(options.SysAdminPassword))
        {
            throw new InvalidOperationException(
                "Bootstrap:Enabled is true but one or more required Bootstrap values are missing - " +
                "SysAdminFirstName, SysAdminLastName, SysAdminEmail and SysAdminPassword must all be set.");
        }
        var requiredRoleNames = new[]
{
    "Member",
    "ResourceApprover",
    "TenantAdmin",
    "SysAdmin"
};

var existingRoleNames = await dbContext.Roles
    .Select(role => role.Name)
    .ToListAsync(cancellationToken);

foreach (var roleName in requiredRoleNames)
{
    if (!existingRoleNames.Contains(roleName))
    {
        dbContext.Roles.Add(new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName
        });
    }
}

await dbContext.SaveChangesAsync(cancellationToken);

        // Idempotent across restarts/redeploys: a user with this email already existing anywhere (not
        // just in a resolvable tenant - IgnoreQueryFilters, the same sanctioned exception
        // UserRepository.FindByEmailAsync uses for the same reason: email is globally unique, and no
        // tenant context exists yet at this point in startup) means a previous run already completed.
        var alreadyBootstrapped = await dbContext.Users.IgnoreQueryFilters()
            .AnyAsync(user => user.Email == options.SysAdminEmail, cancellationToken);
        if (alreadyBootstrapped)
        {
            logger.LogInformation("Bootstrap skipped - a user with the configured Bootstrap:SysAdminEmail already exists.");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // The Roles catalog has no seed migration (see docs/user-administration.md) - reused if it
        // already exists (e.g. a prior tenant's invitation flow referenced it first), created fresh
        // otherwise.
        var sysAdminRole = await dbContext.Roles.FirstOrDefaultAsync(role => role.Name == SysAdminRoleName, cancellationToken);
        if (sysAdminRole is null)
        {
            sysAdminRole = new Role { Id = Guid.NewGuid(), Name = SysAdminRoleName };
            dbContext.Roles.Add(sysAdminRole);
        }

        var sysAdmin = new User
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            FirstName = options.SysAdminFirstName,
            LastName = options.SysAdminLastName,
            Email = options.SysAdminEmail,
            PasswordHash = passwordHasher.Hash(options.SysAdminPassword),
            Status = UserStatus.Active,
            CreatedAtUtc = now,
        };
        dbContext.Users.Add(sysAdmin);
        dbContext.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = sysAdmin.Id, RoleId = sysAdminRole.Id });

        await dbContext.SaveChangesAsync(cancellationToken);
        // Never logs the password or the user's id together with anything sensitive - matches the
        // Options type's own doc comment on removing the secret after first login.
        logger.LogInformation("Bootstrap complete - created the global SysAdmin {SysAdminEmail}.", sysAdmin.Email);
    }
}
