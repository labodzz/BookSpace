using BookSpace.Application.Mediator;
using BookSpace.Application.Security;

namespace BookSpace.Application.Users;

public sealed record GetCurrentUserQuery : IRequest<CurrentUserResponse>;

public sealed record CurrentUserResponse(Guid? UserId, Guid? TenantId, IReadOnlyCollection<string> Roles);

public sealed class GetCurrentUserQueryHandler(ICurrentUserContext currentUserContext)
    : IRequestHandler<GetCurrentUserQuery, CurrentUserResponse>
{
    public Task<CurrentUserResponse> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new CurrentUserResponse(
            currentUserContext.UserId,
            currentUserContext.TenantId,
            currentUserContext.Roles));
}
