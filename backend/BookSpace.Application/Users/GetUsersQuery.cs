using BookSpace.Application.Auth;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Users;

public sealed record GetUsersQuery : IRequest<IReadOnlyList<UserSummaryResponse>>;

public sealed record UserSummaryResponse(Guid Id, string FirstName, string LastName, string Email, Guid TenantId);

public sealed class GetUsersQueryHandler(IUserRepository userRepository)
    : IRequestHandler<GetUsersQuery, IReadOnlyList<UserSummaryResponse>>
{
    public async Task<IReadOnlyList<UserSummaryResponse>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        var users = await userRepository.GetAllAsync(cancellationToken);

        return users
            .Select(user => new UserSummaryResponse(user.Id, user.FirstName, user.LastName, user.Email, user.TenantId))
            .ToList();
    }
}
