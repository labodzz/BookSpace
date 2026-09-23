using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Users;

public sealed record ReactivateUserCommandRequest(Guid Id) : IRequest<ReactivateUserResponse>;

public sealed record ReactivateUserResponse(
    Guid Id, string FirstName, string LastName, string Email, Guid TenantId, UserStatus Status, IReadOnlyList<string> Roles);

// Its own explicit action, not a side effect of UpdateUser - the same reasoning
// docs/resource-lifecycle-and-capacity.md already gives for why Resource reactivation, if it's ever
// built, must be a dedicated action rather than relaxing Update's own check. Unlike Deactivate,
// deliberately NOT idempotent and NOT valid from every non-target status: only Inactive -> Active makes
// sense as "reactivation". Active is a genuine no-op request (nothing to reactivate), and Invited is a
// different transition entirely (accepting the invitation, not an admin action) - both are rejected
// with a clear reason rather than silently doing nothing or guessing what the caller meant.
public sealed class ReactivateUserCommandHandler(IUserRepository userRepository)
    : IRequestHandler<ReactivateUserCommandRequest, ReactivateUserResponse>
{
    public async Task<ReactivateUserResponse> Handle(ReactivateUserCommandRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"User {request.Id} was not found.", ErrorCodes.UserNotFound);

        if (user.Status != UserStatus.Inactive)
        {
            var reason = user.Status == UserStatus.Active
                ? "is already active."
                : "has not accepted their invitation yet - it must be accepted, not reactivated.";
            throw new ConflictException($"User {request.Id} {reason}", ErrorCodes.UserStatusConflict);
        }

        user.Status = UserStatus.Active;
        await userRepository.SaveChangesAsync(cancellationToken);

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        return new ReactivateUserResponse(user.Id, user.FirstName, user.LastName, user.Email, user.TenantId, user.Status, roles);
    }
}
