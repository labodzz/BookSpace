using System.Text.Json;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Xunit;

namespace BookSpace.Application.Tests.Notifications;

public sealed class TenantNotificationFactoryTests
{
    private static Tenant CreateTenant() => new() { Id = Guid.NewGuid(), Name = "Acme Coworking", DefaultTimeZoneId = "UTC", CreatedAtUtc = DateTimeOffset.UtcNow };

    private static User CreateAdminUser(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, FirstName = "Ana", LastName = "Anic",
        Email = "ana@acme.test", PasswordHash = string.Empty, Status = UserStatus.Invited, CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Invitation CreateInvitation(Guid tenantId, Guid userId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, TokenHash = "hash",
        CreatedByUserId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(72),
    };

    [Fact]
    public void AdminInvitation_BuildsARequestWithTheTenantAsOwnerAndTheAdminUserAsRecipient()
    {
        var tenant = CreateTenant();
        var adminUser = CreateAdminUser(tenant.Id);
        var invitation = CreateInvitation(tenant.Id, adminUser.Id);

        var request = TenantNotificationFactory.AdminInvitation(tenant, adminUser, invitation, "https://bookspace.test/accept-invitation?token=abc");

        Assert.Equal(tenant.Id, request.TenantId);
        Assert.Equal(adminUser.Id, request.RecipientUserId);
        Assert.Equal(TenantNotificationTypes.AdminInvitation, request.NotificationType);
    }

    [Fact]
    public void AdminInvitation_UsesTheInvitationsOwnIdAsTheIdempotencyKey_NotTheUsersOrTenants()
    {
        var tenant = CreateTenant();
        var adminUser = CreateAdminUser(tenant.Id);
        var invitation = CreateInvitation(tenant.Id, adminUser.Id);

        var request = TenantNotificationFactory.AdminInvitation(tenant, adminUser, invitation, "https://bookspace.test/accept-invitation?token=abc");

        Assert.Equal($"tenant-admin-invitation:{invitation.Id}", request.IdempotencyKey);
    }

    // The whole point of keying on Invitation.Id rather than the user/tenant: a resend creates a brand new
    // Invitation row with its own fresh id, so its notification must get its OWN idempotency key, never
    // colliding with the first invitation's - otherwise NotificationOutboxWriter would report the resend's
    // notification as "already exists" and silently swallow it. See ResendTenantAdminInvitationCommandRequest.
    [Fact]
    public void AdminInvitation_ForTwoDifferentInvitationsToTheSameUser_ProducesTwoDifferentIdempotencyKeys()
    {
        var tenant = CreateTenant();
        var adminUser = CreateAdminUser(tenant.Id);
        var firstInvitation = CreateInvitation(tenant.Id, adminUser.Id);
        var secondInvitation = CreateInvitation(tenant.Id, adminUser.Id);

        var firstRequest = TenantNotificationFactory.AdminInvitation(tenant, adminUser, firstInvitation, "https://bookspace.test/accept-invitation?token=first");
        var secondRequest = TenantNotificationFactory.AdminInvitation(tenant, adminUser, secondInvitation, "https://bookspace.test/accept-invitation?token=second");

        Assert.NotEqual(firstRequest.IdempotencyKey, secondRequest.IdempotencyKey);
    }

    [Fact]
    public void AdminInvitation_PayloadNeverContainsAPasswordAndCarriesTheInvitationUrlVerbatim()
    {
        var tenant = CreateTenant();
        var adminUser = CreateAdminUser(tenant.Id);
        var invitation = CreateInvitation(tenant.Id, adminUser.Id);
        const string invitationUrl = "https://bookspace.test/accept-invitation?token=raw-token-value";

        var request = TenantNotificationFactory.AdminInvitation(tenant, adminUser, invitation, invitationUrl);
        var payload = JsonSerializer.Deserialize<TenantAdminInvitationNotificationPayload>(request.PayloadJson)!;

        Assert.Equal(invitationUrl, payload.InvitationUrl);
        Assert.Equal(tenant.Name, payload.TenantName);
        Assert.Equal(adminUser.FirstName, payload.RecipientFirstName);
        Assert.Equal(invitation.ExpiresAtUtc, payload.ExpiresAtUtc);
        Assert.DoesNotContain("password", request.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
}
