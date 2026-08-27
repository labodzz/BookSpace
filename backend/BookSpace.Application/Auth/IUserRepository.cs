using BookSpace.Domain.Entities;

namespace BookSpace.Application.Auth;

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken);
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken cancellationToken);

    // Relies on the DbContext's global tenant query filter to scope results - callers must not
    // add their own TenantId filter on top of this.
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken);
}
