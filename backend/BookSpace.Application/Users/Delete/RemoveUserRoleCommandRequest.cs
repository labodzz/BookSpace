using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Users;

public sealed record RemoveUserRoleCommandRequest(Guid UserId, string Role) : IRequest<Unit>;

public sealed class RemoveUserRoleCommandRequestValidator : AbstractValidator<RemoveUserRoleCommandRequest>
{
    public RemoveUserRoleCommandRequestValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Role)
            .Must(role => UserAdministrationGuard.DelegableRoles.Contains(role))
            .WithMessage($"Role must be one of: {nameof(UserAdministrationGuard.DelegableRoles)}.");
    }
}

public sealed class RemoveUserRoleCommandHandler(
    IUserRepository userRepository, IResourceApproverRepository resourceApproverRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<RemoveUserRoleCommandRequest, Unit>
{
    public async Task<Unit> Handle(RemoveUserRoleCommandRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {request.UserId} was not found.", ErrorCodes.UserNotFound);

        var role = await userRepository.FindRoleByNameAsync(request.Role, cancellationToken)
            ?? throw new NotFoundException($"Role '{request.Role}' was not found.", ErrorCodes.RoleNotFound);

        var currentRoles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        if (!currentRoles.Contains(request.Role))
        {
            throw new NotFoundException(
                $"User {request.UserId} does not hold the '{request.Role}' role.", ErrorCodes.UserRoleNotAssigned);
        }

        // Removing your own TenantAdmin/SysAdmin role is a form of self-lockout, same as deactivating
        // your own account - only checked for the two administrative roles: dropping your own Member or
        // Approver role is never a lockout concern. Both checks below only ever apply to
        // TenantAdmin/SysAdmin removal; UserAdministrationGuard.IsAdministrator([...]) on a non-admin
        // role is always false, so a Member/Approver removal short-circuits past both for free.
        if (UserAdministrationGuard.IsAdministrator([request.Role]))
        {
            UserAdministrationGuard.EnsureNotActingOnOwnAccount(request.UserId, currentUserContext.UserId, "remove your own administrative role from");
        }

        // A per-resource ResourceApprover assignment is only meaningful for a user who still holds a
        // role AssignResourceApproverCommandHandler's own eligibility check would accept (Approver,
        // TenantAdmin, or SysAdmin) - see that handler's identical check. Removing the global Approver
        // role while assignments still exist would silently strand them: still listed as an approver on
        // every one of those resources, unable to actually act on any of it, with no signal to the admin
        // that cleanup is needed first. Scoped to "Approver" specifically, not every role: TenantAdmin/
        // SysAdmin are separately guarded administrative roles, not the role this assignment tracks.
        if (request.Role == "Approver")
        {
            var assignedResourceIds = await resourceApproverRepository.GetResourceIdsByUserAsync(request.UserId, cancellationToken);
            if (assignedResourceIds.Count > 0)
            {
                throw new ConflictException(
                    $"User {request.UserId} is still assigned as an approver for {assignedResourceIds.Count} resource(s). " +
                    "Remove those resource assignments first.",
                    ErrorCodes.UserApproverAssignmentsExist);
            }
        }

        var rolesAfterRemoval = currentRoles.Where(r => r != request.Role).ToList();
        await UserAdministrationGuard.EnsureTenantRetainsAnActiveAdministratorAsync(
            targetCountsBeforeChange: user.Status == UserStatus.Active && UserAdministrationGuard.IsAdministrator(currentRoles),
            targetCountsAfterChange: user.Status == UserStatus.Active && UserAdministrationGuard.IsAdministrator(rolesAfterRemoval),
            userRepository,
            cancellationToken);

        await userRepository.RemoveRoleAsync(user.Id, role.Id, cancellationToken);
        await userRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
