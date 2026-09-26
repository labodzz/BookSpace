using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Creates the first tenant and first TenantAdmin in an otherwise-empty production database - the
// production counterpart to DevelopmentSeeder, gated by BootstrapOptions.Enabled instead of the
// Development environment check. Called from Program.cs only after migrations have already run (see
// DependencyInjection.BootstrapProductionAdminAsync), never in place of them and never alongside
// DevelopmentSeeder - the two are mutually exclusive by construction (one runs only in Development,
// this one only when explicitly enabled by configuration).
internal static class ProductionBootstrapper
{
    private const string TenantAdminRoleName = "TenantAdmin";

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

        if (string.IsNullOrWhiteSpace(options.TenantName)
            || string.IsNullOrWhiteSpace(options.DefaultTimeZoneId)
            || string.IsNullOrWhiteSpace(options.AdminFirstName)
            || string.IsNullOrWhiteSpace(options.AdminLastName)
            || string.IsNullOrWhiteSpace(options.AdminEmail)
            || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            throw new InvalidOperationException(
                "Bootstrap:Enabled is true but one or more required Bootstrap values are missing - " +
                "TenantName, DefaultTimeZoneId, AdminFirstName, AdminLastName, AdminEmail and AdminPassword " +
                "must all be set.");
        }

        // Idempotent across restarts/redeploys: a user with this email already existing anywhere (not
        // just in a resolvable tenant - IgnoreQueryFilters, the same sanctioned exception
        // UserRepository.FindByEmailAsync uses for the same reason: email is globally unique, and no
        // tenant context exists yet at this point in startup) means a previous run already completed.
        var alreadyBootstrapped = await dbContext.Users.IgnoreQueryFilters()
            .AnyAsync(user => user.Email == options.AdminEmail, cancellationToken);
        if (alreadyBootstrapped)
        {
            logger.LogInformation("Bootstrap skipped - a user with the configured Bootstrap:AdminEmail already exists.");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = options.TenantName,
            DefaultTimeZoneId = options.DefaultTimeZoneId,
            Status = TenantStatus.Active,
            CreatedAtUtc = now,
        };
        dbContext.Tenants.Add(tenant);

        // The Roles catalog has no seed migration (see docs/user-administration.md) - reused if a prior
        // bootstrap or another tenant already created it, created fresh otherwise.
        var tenantAdminRole = await dbContext.Roles.FirstOrDefaultAsync(role => role.Name == TenantAdminRoleName, cancellationToken);
        if (tenantAdminRole is null)
        {
            tenantAdminRole = new Role { Id = Guid.NewGuid(), Name = TenantAdminRoleName };
            dbContext.Roles.Add(tenantAdminRole);
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FirstName = options.AdminFirstName,
            LastName = options.AdminLastName,
            Email = options.AdminEmail,
            PasswordHash = passwordHasher.Hash(options.AdminPassword),
            Status = UserStatus.Active,
            CreatedAtUtc = now,
        };
        dbContext.Users.Add(admin);
        dbContext.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = admin.Id, RoleId = tenantAdminRole.Id });

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Bootstrap complete - created tenant {TenantName} and its first TenantAdmin.", tenant.Name);
    }
}
