using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Users;

// Deliberately narrow: FirstName/LastName only - the two "basic user data" fields the current User
// entity actually has beyond Email (which is globally unique and immutable here - changing a user's
// email is a distinct, higher-stakes operation with its own verification concerns not asked for by
// this batch) and Status/roles (each already their own dedicated action - see
// docs/resource-lifecycle-and-capacity.md's "own explicit lifecycle action" reasoning, applied the same
// way to User here).
public sealed record UpdateUserCommandRequest(Guid Id, string FirstName, string LastName) : IRequest<UpdateUserResponse>;

public sealed record UpdateUserResponse(
    Guid Id, string FirstName, string LastName, string Email, Guid TenantId, UserStatus Status, IReadOnlyList<string> Roles);

public sealed class UpdateUserCommandRequestValidator : AbstractValidator<UpdateUserCommandRequest>
{
    public UpdateUserCommandRequestValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.LastName).NotEmpty().MaximumLength(100);
    }
}

public sealed class UpdateUserCommandHandler(IUserRepository userRepository)
    : IRequestHandler<UpdateUserCommandRequest, UpdateUserResponse>
{
    public async Task<UpdateUserResponse> Handle(UpdateUserCommandRequest request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"User {request.Id} was not found.", ErrorCodes.UserNotFound);

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        await userRepository.SaveChangesAsync(cancellationToken);

        var roles = await userRepository.GetRolesAsync(user.Id, cancellationToken);
        return new UpdateUserResponse(user.Id, user.FirstName, user.LastName, user.Email, user.TenantId, user.Status, roles);
    }
}
