using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Users;

public sealed record AssignUserRoleCommandRequest(Guid UserId, string Role) : IRequest<AssignUserRoleResponse>;

public sealed record AssignUserRoleResponse(Guid UserId, IReadOnlyList<string> Roles);

public sealed class AssignUserRoleCommandRequestValidator : AbstractValidator<AssignUserRoleCommandRequest>
{
    public AssignUserRoleCommandRequestValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Role)
            .Must(role => UserAdministrationGuard.DelegableRoles.Contains(role))
            .WithMessage($"Role must be one of: {nameof(UserAdministrationGuard.DelegableRoles)}.");
    }
}

// Assigning/removing a role only affects the NEXT access token this user's session issues (login, or
// their next refresh) - roles are baked into the JWT at issuance and this codebase has no mechanism to
// invalidate an already-issued token early, an accepted tradeoff bounded by AccessTokenMinutes (15).
// See docs/open-questions.md's "Dynamic Authorization" entry and docs/user-administration.md.
public sealed class AssignUserRoleCommandHandler(IUserRepository userRepository)
    : IRequestHandler<AssignUserRoleCommandRequest, AssignUserRoleResponse>
{
    public async Task<AssignUserRoleResponse> Handle(AssignUserRoleCommandRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {request.UserId} was not found.", ErrorCodes.UserNotFound);

        var role = await userRepository.FindRoleByNameAsync(request.Role, cancellationToken)
            ?? throw new NotFoundException($"Role '{request.Role}' was not found.", ErrorCodes.RoleNotFound);

        var currentRoles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        if (currentRoles.Contains(request.Role))
        {
            throw new ConflictException($"User {request.UserId} already holds the '{request.Role}' role.", ErrorCodes.UserRoleConflict);
        }

        await userRepository.AddRoleAsync(user.Id, role.Id, cancellationToken);
        await userRepository.SaveChangesAsync(cancellationToken);

        return new AssignUserRoleResponse(user.Id, [.. currentRoles, request.Role]);
    }
}
