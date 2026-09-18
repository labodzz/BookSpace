using BookSpace.Application.Mediator;
using BookSpace.Application.Security;

namespace BookSpace.Application.Users;

public sealed record GetCurrentUserQueryRequest : IRequest<CurrentUserResponse>;

public sealed record CurrentUserResponse(Guid? UserId, Guid? TenantId, IReadOnlyCollection<string> Roles);

public sealed class GetCurrentUserQueryRequestHandler(ICurrentUserContext currentUserContext)
    : IRequestHandler<GetCurrentUserQueryRequest, CurrentUserResponse>
{
    public Task<CurrentUserResponse> Handle(GetCurrentUserQueryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CurrentUserResponse(
            currentUserContext.UserId,
            currentUserContext.TenantId,
            currentUserContext.Roles));
}
