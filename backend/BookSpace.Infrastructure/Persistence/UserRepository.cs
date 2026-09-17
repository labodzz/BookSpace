using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class UserRepository(BookSpaceDbContext dbContext) : IUserRepository
{
    // Explicitly bypasses the tenant query filter: email is globally unique (see the index comment on
    // User in BookSpaceModelConfiguration.cs), and login is the one moment there's no tenant context
    // yet to filter by - the tenant is only known once this lookup finds the user. This is the sole
    // sanctioned exception to "every ITenantOwned read goes through the tenant filter"; don't copy this
    // pattern for anything that isn't a pre-authentication, globally-unique-key lookup.
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    // See the interface doc comment - explicitly bypasses the tenant filter for the one caller
    // (AuthenticationService.RefreshAsync) that has no tenant context yet.
    public Task<User?> FindByIdForAuthenticationAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .Join(dbContext.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => role.Name)
            .ToListAsync(cancellationToken);

    public async Task<PagedResult<User>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Users.OrderBy(user => user.Email);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<User>(items, page, pageSize, totalCount);
    }
}
