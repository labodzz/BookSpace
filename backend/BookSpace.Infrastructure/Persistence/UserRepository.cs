using BookSpace.Application.Auth;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal sealed class UserRepository(BookSpaceDbContext dbContext) : IUserRepository
{
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .Join(dbContext.Roles, userRole => userRole.RoleId, role => role.Id, (userRole, role) => role.Name)
            .ToListAsync(cancellationToken);
}
