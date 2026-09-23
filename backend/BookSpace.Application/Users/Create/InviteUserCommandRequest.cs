using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Users;

public sealed record InviteUserCommandRequest(string Email, string FirstName, string LastName, IReadOnlyList<string> Roles)
    : IRequest<InviteUserResponse>;

// InvitationToken is the ONE and ONLY time the raw token is ever returned or storable anywhere - only
// its SHA-256 hash is persisted (Invitation.TokenHash). No email-delivery infrastructure exists in this
// codebase yet (see docs/architecture.md's "not yet implemented at all" list), so returning it directly
// to the authorized caller is the interim workflow - see docs/user-administration.md.
public sealed record InviteUserResponse(Guid UserId, string Email, string InvitationToken, DateTimeOffset InvitationExpiresAtUtc);

public sealed class InviteUserCommandRequestValidator : AbstractValidator<InviteUserCommandRequest>
{
    public InviteUserCommandRequestValidator()
    {
        RuleFor(command => command.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(command => command.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.LastName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.Roles).NotNull();
        RuleForEach(command => command.Roles)
            .Must(role => UserAdministrationGuard.DelegableRoles.Contains(role))
            .WithMessage($"Role must be one of: {nameof(UserAdministrationGuard.DelegableRoles)}.");
    }
}

// Invitation expiry: 72 hours. Not configurable yet - no existing precedent in this codebase for a
// tenant-tunable expiry window comparable to Tenant.ApprovalExpiryHours, and inventing one without a
// concrete requirement would be guessing; a fixed, documented default is the conservative choice this
// batch was asked for. See docs/user-administration.md.
public sealed class InviteUserCommandHandler(
    IUserRepository userRepository, IInvitationRepository invitationRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<InviteUserCommandRequest, InviteUserResponse>
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    public async Task<InviteUserResponse> Handle(InviteUserCommandRequest request, CancellationToken cancellationToken)
    {
        // Global lookup (bypasses the tenant filter, like every other email-uniqueness check in this
        // codebase) - Email is a globally unique column, and a conflict here must read identically
        // whether the email belongs to an active user in THIS tenant or ANY other one, so cross-tenant
        // existence is never revealed.
        var existingUser = await userRepository.FindByEmailAsync(request.Email, cancellationToken);

        User user;
        if (existingUser is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = currentUserContext.TenantId!.Value,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                PasswordHash = string.Empty,
                Status = UserStatus.Invited,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            await userRepository.AddAsync(user, cancellationToken);

            foreach (var roleName in request.Roles.Distinct())
            {
                var role = await userRepository.FindRoleByNameAsync(roleName, cancellationToken)
                    ?? throw new NotFoundException($"Role '{roleName}' was not found.", ErrorCodes.RoleNotFound);
                await userRepository.AddRoleAsync(user.Id, role.Id, cancellationToken);
            }
        }
        else if (existingUser.Status == UserStatus.Invited)
        {
            // Reissue: the same pending user, a fresh token. Roles are deliberately NOT re-applied here
            // - adjusting an already-staged invitation's roles is what AssignUserRole/RemoveUserRole are
            // for, not something a reissue silently redoes with whatever list happens to be resubmitted.
            user = existingUser;
        }
        else
        {
            // Active or Inactive - a real account already exists under this email, in this tenant or
            // another. One generic conflict regardless of which: never reveal that distinction.
            throw new ConflictException("A user with this email already exists.", ErrorCodes.UserEmailConflict);
        }

        var previousActiveInvitation = await invitationRepository.FindActiveByUserIdAsync(user.Id, cancellationToken);
        if (previousActiveInvitation is not null)
        {
            previousActiveInvitation.RevokedAtUtc = DateTimeOffset.UtcNow;
        }

        var rawToken = SecureTokenGenerator.GenerateRawToken();
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            UserId = user.Id,
            TokenHash = SecureTokenGenerator.HashToken(rawToken),
            CreatedByUserId = currentUserContext.UserId!.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(InvitationLifetime),
        };
        await invitationRepository.AddAsync(invitation, cancellationToken);
        await invitationRepository.SaveChangesAsync(cancellationToken);

        return new InviteUserResponse(user.Id, user.Email, rawToken, invitation.ExpiresAtUtc);
    }
}
