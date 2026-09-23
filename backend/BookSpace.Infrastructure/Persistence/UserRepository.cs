using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class UserRepository(BookSpaceDbContext dbContext) : IUserRepository
{
    // Administrative roles that count toward "this tenant must always retain at least one active
    // administrator" - see CountActiveAdministratorsAsync and UserAdministrationGuard.
    private static readonly string[] AdministratorRoleNames = ["TenantAdmin", "SysAdmin"];

    // Explicitly bypasses the tenant query filter: email is globally unique (see the index comment on
    // User in BookSpaceModelConfiguration.cs), and login is the one moment there's no tenant context
    // yet to filter by - the tenant is only known once this lookup finds the user. This is the sole
    // sanctioned exception to "every ITenantOwned read goes through the tenant filter"; don't copy this
    // pattern for anything that isn't a pre-authentication, globally-unique-key lookup.
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    // See the interface doc comment - explicitly bypasses the tenant filter for the callers (refresh,
    // accept-invitation) that have no tenant context yet.
    public Task<User?> FindByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .Join(dbContext.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => role.Name)
            .ToListAsync(cancellationToken);

    // search/role/ids/status are all applied on top of dbContext.Users, which already carries the
    // global tenant query filter - a cross-tenant user can never appear in the result regardless of
    // which optional filters are supplied. search/role use plain string.Contains/correlated-subquery
    // shapes that EF Core translates directly to SQL (LIKE / EXISTS), never evaluated client-side.
    public async Task<PagedResult<User>> GetPagedAsync(
        int page, int pageSize, string? search, string? role, IReadOnlyList<Guid>? ids, UserStatus? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(user =>
                user.FirstName.Contains(search) || user.LastName.Contains(search) || user.Email.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(user => dbContext.UserRoles
                .Join(dbContext.Roles, userRole => userRole.RoleId, r => r.Id, (userRole, r) => new { userRole.UserId, r.Name })
                .Any(userRole => userRole.UserId == user.Id && userRole.Name == role));
        }

        if (ids is { Count: > 0 })
        {
            query = query.Where(user => ids.Contains(user.Id));
        }

        if (status is not null)
        {
            query = query.Where(user => user.Status == status);
        }

        query = query.OrderBy(user => user.Email);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<User>(items, page, pageSize, totalCount);
    }

    // One query for the whole batch (a single join + group-by), never one GetRolesAsync call per user -
    // see the interface doc comment for why this exists as its own method rather than callers looping
    // GetRolesAsync themselves.
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRolesByUserIdsAsync(
        IReadOnlyList<Guid> userIds, CancellationToken cancellationToken)
    {
        var rows = await dbContext.UserRoles
            .Where(userRole => userIds.Contains(userRole.UserId))
            .Join(dbContext.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.UserId)
            .ToDictionary(group => group.Key, IReadOnlyList<string> (group) => group.Select(row => row.Name).ToList());
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken) =>
        await dbContext.Users.AddAsync(user, cancellationToken);

    public Task<Role?> FindRoleByNameAsync(string name, CancellationToken cancellationToken) =>
        dbContext.Roles.FirstOrDefaultAsync(role => role.Name == name, cancellationToken);

    public async Task AddRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken) =>
        await dbContext.UserRoles.AddAsync(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId }, cancellationToken);

    // A no-op (not an error) if the user doesn't currently hold this role - see the interface doc
    // comment for why callers are expected to check GetRolesAsync themselves first when that
    // distinction matters.
    public async Task RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var userRole = await dbContext.UserRoles
            .FirstOrDefaultAsync(userRole => userRole.UserId == userId && userRole.RoleId == roleId, cancellationToken);
        if (userRole is not null)
        {
            dbContext.UserRoles.Remove(userRole);
        }
    }

    // Tenant-scoped for free: dbContext.Users already carries the global query filter, so this can
    // never count another tenant's administrators. Invited/Inactive holders of an admin role are
    // deliberately excluded by the Status == Active check - see the interface doc comment.
    public Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken) =>
        dbContext.Users
            .Where(user => user.Status == UserStatus.Active)
            .Where(user => dbContext.UserRoles
                .Join(dbContext.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => new { userRole.UserId, role.Name })
                .Any(userRole => userRole.UserId == user.Id && AdministratorRoleNames.Contains(userRole.Name)))
            .CountAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesHandlingConflictsAsync(cancellationToken);
}
