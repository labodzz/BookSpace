using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Tenants;

// SysAdmin-only platform recovery action (see docs/tenant-onboarding.md, "Tenant lifecycle" /
// UserAdministrationGuard.DelegableRoles) for a tenant that has lost its administrator - or simply needs
// a second one. Deliberately NOT InviteUserCommandHandler reused with a different TenantId:
// InviteUserCommandHandler hardcodes currentUserContext.TenantId (always the CALLER's own tenant, never
// usable by a global SysAdmin whose TenantId is null) and returns the raw invitation token directly in
// its HTTP response instead of emailing it - exactly the opposite of what a SysAdmin-facing recovery
// action needs here: a real email through the same outbox/template/URL path CreateTenantCommandHandler's
// first-admin invitation already uses. Reusing TenantNotificationFactory.AdminInvitation directly means
// the recipient's email reads identically whether they're the first or a replacement TenantAdmin - there
// is nothing meaningfully different about the invitation itself.
public sealed record InviteReplacementTenantAdminCommandRequest(Guid TenantId, string FirstName, string LastName, string Email)
    : IRequest<InviteReplacementTenantAdminResponse>;

// Never includes the invitation token or its URL - same rule as CreateTenantResponse.
public sealed record InviteReplacementTenantAdminResponse(Guid TenantId, Guid AdminUserId, string AdminEmail);

public sealed class InviteReplacementTenantAdminCommandRequestValidator : AbstractValidator<InviteReplacementTenantAdminCommandRequest>
{
    public InviteReplacementTenantAdminCommandRequestValidator()
    {
        RuleFor(command => command.TenantId).NotEmpty();
        RuleFor(command => command.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.LastName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

// Same atomic-commit shape as CreateTenantCommandHandler, minus creating the Tenant itself (it already
// exists) - User, UserRole, and Invitation are all staged on this DbContext and committed together by
// NotificationOutboxWriter.EnqueueAsync's own internal SaveChangesAsync.
public sealed class InviteReplacementTenantAdminCommandHandler(
    ITenantRepository tenantRepository,
    IUserRepository userRepository,
    IInvitationRepository invitationRepository,
    INotificationOutboxWriter notificationOutboxWriter,
    ICurrentUserContext currentUserContext,
    IOptions<AppOptions> appOptions)
    : IRequestHandler<InviteReplacementTenantAdminCommandRequest, InviteReplacementTenantAdminResponse>
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    public async Task<InviteReplacementTenantAdminResponse> Handle(
        InviteReplacementTenantAdminCommandRequest request, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.FindByIdAsync(request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant {request.TenantId} was not found.", ErrorCodes.TenantNotFound);

        if (tenant.Status != TenantStatus.Active)
        {
            throw new ConflictException(
                $"Tenant {request.TenantId} is not Active (current status: {tenant.Status}) - cannot invite an administrator into it.",
                ErrorCodes.TenantStatusConflict);
        }

        // Global lookup (bypasses the tenant filter), same reasoning as CreateTenantCommandHandler's own
        // identical check - never reveals whether the existing account belongs to this tenant or another.
        if (await userRepository.FindByEmailAsync(request.Email, cancellationToken) is not null)
        {
            throw new ConflictException("A user with this email already exists.", ErrorCodes.UserEmailConflict);
        }

        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            PasswordHash = string.Empty,
            Status = UserStatus.Invited,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await userRepository.AddAsync(adminUser, cancellationToken);

        var tenantAdminRole = await userRepository.FindRoleByNameAsync("TenantAdmin", cancellationToken)
            ?? throw new NotFoundException("Role 'TenantAdmin' was not found.", ErrorCodes.RoleNotFound);
        await userRepository.AddRoleAsync(adminUser.Id, tenantAdminRole.Id, cancellationToken);

        var rawToken = SecureTokenGenerator.GenerateRawToken();
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = adminUser.Id,
            TokenHash = SecureTokenGenerator.HashToken(rawToken),
            CreatedByUserId = currentUserContext.UserId!.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(InvitationLifetime),
        };
        await invitationRepository.AddAsync(invitation, cancellationToken);

        var invitationUrl = TenantInvitationUrlBuilder.Build(appOptions.Value.PublicBaseUrl!, rawToken);
        var outboxRequest = TenantNotificationFactory.AdminInvitation(tenant, adminUser, invitation, invitationUrl);
        await notificationOutboxWriter.EnqueueAsync(outboxRequest, cancellationToken);

        return new InviteReplacementTenantAdminResponse(tenant.Id, adminUser.Id, adminUser.Email);
    }
}
