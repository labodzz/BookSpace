using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Notifications;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.Tenants;

// SysAdmin only - see docs/tenant-onboarding.md. Only the fields actually required to start a tenant are
// collected here; every other Tenant column (ReminderLeadMinutes, NoShowGraceMinutes, ApprovalExpiryHours,
// CheckInLeadMinutes) keeps its existing C# default (see Tenant.cs) - this form does not re-litigate those
// already-established defaults, and nothing stops the new TenantAdmin from being given a way to tune them
// later (out of this task's scope).
public sealed record CreateTenantCommandRequest(string Name, string DefaultTimeZoneId, string AdminFirstName, string AdminLastName, string AdminEmail)
    : IRequest<CreateTenantResponse>;

// Never includes the invitation token or its URL - see docs/tenant-onboarding.md ("Where the raw token
// lives, and for how long"). AdminUserId is returned specifically so the frontend's own "resend
// invitation" action (ResendTenantAdminInvitationCommandRequest) can target this exact user without the
// backend needing an ambiguous "find the pending admin for this tenant" lookup.
public sealed record CreateTenantResponse(Guid TenantId, string TenantName, Guid AdminUserId, string AdminEmail);

public sealed class CreateTenantCommandRequestValidator : AbstractValidator<CreateTenantCommandRequest>
{
    public CreateTenantCommandRequestValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.DefaultTimeZoneId).NotEmpty().MaximumLength(100).Must(TimeZoneValidation.BeAValidTimeZoneId)
            .WithMessage("DefaultTimeZoneId must be a valid IANA time zone identifier.");
        RuleFor(command => command.AdminFirstName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.AdminLastName).NotEmpty().MaximumLength(100);
        RuleFor(command => command.AdminEmail).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

// Creates a Tenant, its first TenantAdmin (Status = Invited, same "exists before a password does"
// convention InviteUserCommandHandler already established), the TenantAdmin role assignment, and the
// invitation, all staged on the SAME DbContext and committed together by the LAST write in this method
// (notificationOutboxWriter.EnqueueAsync's own internal SaveChangesAsync) - see docs/tenant-onboarding.md
// ("Transaction boundary") for why this is one atomic commit, not four separate ones, and why that
// required checking (not assuming) every repository call below never calls SaveChangesAsync itself.
public sealed class CreateTenantCommandHandler(
    ITenantRepository tenantRepository,
    IUserRepository userRepository,
    IInvitationRepository invitationRepository,
    INotificationOutboxWriter notificationOutboxWriter,
    ICurrentUserContext currentUserContext,
    IOptions<AppOptions> appOptions)
    : IRequestHandler<CreateTenantCommandRequest, CreateTenantResponse>
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    public async Task<CreateTenantResponse> Handle(CreateTenantCommandRequest request, CancellationToken cancellationToken)
    {
        // Friendly pre-checks - the real, race-proof guarantee is the database's own unique constraints
        // (Tenant.Name, User.Email), caught below around the actual commit. See "Concurrent create" in
        // docs/tenant-onboarding.md for why both layers are needed, not just one.
        if (await tenantRepository.ExistsByNameAsync(request.Name, cancellationToken))
        {
            throw new ConflictException($"A tenant named '{request.Name}' already exists.", ErrorCodes.TenantNameConflict);
        }

        // Global lookup (bypasses the tenant filter), same reasoning as InviteUserCommandHandler's own
        // identical check: Email is globally unique, and a conflict here must read identically whether the
        // existing account belongs to this brand-new tenant (impossible - it doesn't exist yet) or ANY
        // other one - never revealing that distinction. Unlike InviteUserCommandHandler, there is no
        // "existing Invited user" reissue branch here: a brand-new tenant can never already have a pending
        // invitation for an email that isn't associated with it yet, so ANY existing user with this email
        // is a straightforward conflict.
        if (await userRepository.FindByEmailAsync(request.AdminEmail, cancellationToken) is not null)
        {
            throw new ConflictException("A user with this email already exists.", ErrorCodes.UserEmailConflict);
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            DefaultTimeZoneId = request.DefaultTimeZoneId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await tenantRepository.AddAsync(tenant, cancellationToken);

        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FirstName = request.AdminFirstName,
            LastName = request.AdminLastName,
            Email = request.AdminEmail,
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

        // The single commit point for all five staged rows (Tenant, User, UserRole, Invitation,
        // NotificationOutboxItem) - see the class doc comment. A genuine race on Tenant.Name/User.Email
        // surfaces as a ConflictException thrown directly by NotificationOutboxWriter itself (it is the
        // only layer here allowed to reference EF Core/SqlClient to detect it) - see its own doc comment.
        await notificationOutboxWriter.EnqueueAsync(outboxRequest, cancellationToken);

        return new CreateTenantResponse(tenant.Id, tenant.Name, adminUser.Id, adminUser.Email);
    }
}
