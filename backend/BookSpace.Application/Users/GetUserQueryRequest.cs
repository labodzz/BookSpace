using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Users;

public sealed record GetUserQueryRequest(Guid Id) : IRequest<GetUserResponse>;

// PendingInvitationExpiresAtUtc is set only when Status is Invited and an active (not accepted, not
// revoked) invitation currently exists for this user - lets the future admin UI show "invited, expires
// <date>" without a separate invitations endpoint. Never carries anything about the invitation's token
// - see InviteUserResponse for the only place the raw token is ever returned.
public sealed record GetUserResponse(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    Guid TenantId,
    UserStatus Status,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PendingInvitationExpiresAtUtc);

public sealed class GetUserQueryHandler(IUserRepository userRepository, IInvitationRepository invitationRepository)
    : IRequestHandler<GetUserQueryRequest, GetUserResponse>
{
    public async Task<GetUserResponse> Handle(GetUserQueryRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"User {request.Id} was not found.", ErrorCodes.UserNotFound);

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);

        DateTimeOffset? pendingInvitationExpiresAtUtc = null;
        if (user.Status == UserStatus.Invited)
        {
            var activeInvitation = await invitationRepository.FindActiveByUserIdAsync(user.Id, cancellationToken);
            pendingInvitationExpiresAtUtc = activeInvitation?.ExpiresAtUtc;
        }

        return new GetUserResponse(
            user.Id, user.FirstName, user.LastName, user.Email, user.TenantId, user.Status, roles, user.CreatedAtUtc,
            pendingInvitationExpiresAtUtc);
    }
}
