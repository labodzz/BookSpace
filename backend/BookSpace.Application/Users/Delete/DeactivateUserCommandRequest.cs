using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Users;

public sealed record DeactivateUserCommandRequest(Guid Id) : IRequest<DeactivateUserResponse>;

public sealed record DeactivateUserResponse(
    Guid Id, string FirstName, string LastName, string Email, Guid TenantId, UserStatus Status, IReadOnlyList<string> Roles);

// A soft "delete" (Status -> Inactive), same DELETE-verb-means-soft-transition convention
// DeleteResourceCommandHandler already uses - a user is never physically removed (see
// docs/user-administration.md; "Do not implement: Deleting users physically" was explicit scope for
// this batch). Idempotent, also matching that same precedent: deactivating an already-Inactive user
// just returns their current state rather than erroring. Valid from either Invited or Active - an admin
// choosing not to onboard someone after all can deactivate a still-pending invitation the same way as
// an established account.
public sealed class DeactivateUserCommandHandler(IUserRepository userRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<DeactivateUserCommandRequest, DeactivateUserResponse>
{
    public async Task<DeactivateUserResponse> Handle(DeactivateUserCommandRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"User {request.Id} was not found.", ErrorCodes.UserNotFound);

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);

        if (user.Status != UserStatus.Inactive)
        {
            UserAdministrationGuard.EnsureNotActingOnOwnAccount(request.Id, currentUserContext.UserId, "deactivate");

            await UserAdministrationGuard.EnsureTenantRetainsAnActiveAdministratorAsync(
                targetCountsBeforeChange: user.Status == UserStatus.Active && UserAdministrationGuard.IsAdministrator(roles),
                targetCountsAfterChange: false,
                userRepository,
                cancellationToken);

            user.Status = UserStatus.Inactive;
            await userRepository.SaveChangesAsync(cancellationToken);
        }

        return new DeactivateUserResponse(user.Id, user.FirstName, user.LastName, user.Email, user.TenantId, user.Status, roles);
    }
}
