using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BookSpace.Api.Tests.Users;

// The user lifecycle/role-administration surface added in Batch 4A: invite, accept-invitation, update,
// deactivate, reactivate, assign/remove role, and single-user detail. GET /users (list) and GET /users/me
// stay in UsersEndpointsTests.cs; this file is the rest of the admin-mutation surface.
public sealed class UserAdministrationEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public UserAdministrationEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    // --- Invite ---

    [Fact]
    public async Task InviteUser_AsTenantAdmin_ReturnsCreatedWithAnInvitationToken()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var email = $"invitee-{Guid.NewGuid()}@acme.integration-test";

        var response = await client.PostAsJsonAsync("/api/users/invitations", new { email, firstName = "New", lastName = "Hire", roles = new[] { "Member" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOptions);
        Assert.Equal(email, body!.Email);
        Assert.False(string.IsNullOrWhiteSpace(body.InvitationToken));
        Assert.True(body.InvitationExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InviteUser_AsSysAdmin_ReturnsCreated()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeSysAdminEmail);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new
        {
            email = $"invitee-{Guid.NewGuid()}@acme.integration-test", firstName = "New", lastName = "Hire", roles = new[] { "Member" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task InviteUser_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new
        {
            email = $"invitee-{Guid.NewGuid()}@acme.integration-test", firstName = "New", lastName = "Hire", roles = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InviteUser_AsApprover_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeApproverEmail);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new
        {
            email = $"invitee-{Guid.NewGuid()}@acme.integration-test", firstName = "New", lastName = "Hire", roles = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InviteUser_WithAnEmailAlreadyUsedInAnotherTenant_ReturnsConflictWithoutRevealingTheTenant()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new
        {
            email = TestDataSeeder.GlobexAdminEmail, firstName = "Someone", lastName = "Else", roles = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.EmailConflict", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task InviteUser_WithSysAdminInTheRolesList_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new
        {
            email = $"invitee-{Guid.NewGuid()}@acme.integration-test", firstName = "New", lastName = "Hire", roles = new[] { "SysAdmin" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Reissuing (inviting the same still-pending email again) must invalidate the previous token - the
    // old link an admin might have already sent out stops working the moment a new one is issued.
    [Fact]
    public async Task InviteUser_ReinvitingAStillPendingEmail_InvalidatesThePreviousInvitationToken()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var email = $"reinvite-{Guid.NewGuid()}@acme.integration-test";
        var firstInvite = await client.PostAsJsonAsync("/api/users/invitations", new { email, firstName = "New", lastName = "Hire", roles = new[] { "Member" } });
        var firstToken = (await firstInvite.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOptions))!.InvitationToken;

        var secondInvite = await client.PostAsJsonAsync("/api/users/invitations", new { email, firstName = "New", lastName = "Hire", roles = new[] { "Member" } });
        Assert.Equal(HttpStatusCode.Created, secondInvite.StatusCode);

        var acceptWithOldToken = await client.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token = firstToken, password = "a-New-Passw0rd!" });

        Assert.Equal(HttpStatusCode.Unauthorized, acceptWithOldToken.StatusCode);
    }

    // --- Accept invitation ---

    [Fact]
    public async Task AcceptInvitation_WithAValidToken_ActivatesTheUserWhoCanThenLogIn()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var email = $"activated-{Guid.NewGuid()}@acme.integration-test";
        var invite = await adminClient.PostAsJsonAsync("/api/users/invitations", new { email, firstName = "New", lastName = "Hire", roles = new[] { "Member" } });
        var invitation = (await invite.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOptions))!;

        using var anonymousClient = _factory.CreateClient();
        var acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token = invitation.InvitationToken, password = "a-New-Passw0rd!" });

        Assert.Equal(HttpStatusCode.NoContent, acceptResponse.StatusCode);
        // No access/refresh token in the response body - login is a separate, subsequent step.
        Assert.Empty(await acceptResponse.Content.ReadAsByteArrayAsync());

        var loginResponse = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email, password = "a-New-Passw0rd!" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WithAnAlreadyUsedToken_ReturnsUnauthorized()
    {
        using var adminClient = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var email = $"reused-{Guid.NewGuid()}@acme.integration-test";
        var invite = await adminClient.PostAsJsonAsync("/api/users/invitations", new { email, firstName = "New", lastName = "Hire", roles = Array.Empty<string>() });
        var token = (await invite.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOptions))!.InvitationToken;
        using var anonymousClient = _factory.CreateClient();
        await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token, password = "a-New-Passw0rd!" });

        var secondAttempt = await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token, password = "a-Different-Passw0rd!" });

        Assert.Equal(HttpStatusCode.Unauthorized, secondAttempt.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WithAnUnknownToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/accept-invitation", new { token = "not-a-real-token", password = "a-New-Passw0rd!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WithATooShortPassword_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/accept-invitation", new { token = "whatever", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Get single user ---

    [Fact]
    public async Task GetUser_AsTenantAdmin_ReturnsFullDetailWithRolesAndStatus()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponse>(JsonOptions);
        Assert.Equal(TestDataSeeder.AcmeApproverEmail, body!.Email);
        Assert.Contains("Approver", body.Roles);
        Assert.Equal(0, body.Status); // UserStatus.Active
    }

    [Fact]
    public async Task GetUser_ForAnotherTenantsUser_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync($"/api/users/{TestDataSeeder.GlobexAdminUserId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Update ---

    [Fact]
    public async Task UpdateUser_AsTenantAdmin_UpdatesTheNameFields()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Original", "Name");

        var response = await client.PutAsJsonAsync($"/api/users/{created.UserId}", new { firstName = "Updated", lastName = "Person" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponse>(JsonOptions);
        Assert.Equal("Updated", body!.FirstName);
        Assert.Equal("Person", body.LastName);
    }

    [Fact]
    public async Task UpdateUser_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PutAsJsonAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}", new { firstName = "Should", lastName = "BeForbidden" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUser_ForAnotherTenantsUser_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PutAsJsonAsync($"/api/users/{TestDataSeeder.GlobexAdminUserId}", new { firstName = "Should", lastName = "NotApply" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Deactivate / Reactivate ---

    [Fact]
    public async Task DeactivateUser_AsTenantAdmin_SetsStatusToInactive()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "To", "Deactivate");

        var response = await client.DeleteAsync($"/api/users/{created.UserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponse>(JsonOptions);
        Assert.Equal(2, body!.Status); // UserStatus.Inactive
    }

    [Fact]
    public async Task DeactivateUser_AlreadyInactive_IsIdempotent()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Already", "Inactive");
        await client.DeleteAsync($"/api/users/{created.UserId}");

        var response = await client.DeleteAsync($"/api/users/{created.UserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponse>(JsonOptions);
        Assert.Equal(2, body!.Status);
    }

    [Fact]
    public async Task DeactivateUser_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeactivateUser_CallerDeactivatingTheirOwnAccount_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeAdminUserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.SelfLockout", await ReadErrorCodeAsync(response));
    }

    // The last-admin guard (UserAdministrationGuard.EnsureTenantRetainsAnActiveAdministratorAsync) is
    // exercised directly at the handler level - see DeactivateUserCommandHandlerTests - not here. A real
    // HTTP caller can only ever reach this endpoint by holding a currently-valid TenantAdmin/SysAdmin JWT
    // claim, which - in the overwhelmingly ordinary case - means that caller themselves is ALSO a
    // currently-active administrator. Since the caller can never be the target (self-lockout is checked
    // first and rejects that), any DIFFERENT, successfully-authorized caller necessarily keeps the active-
    // administrator count at 2 or more for as long as they're calling it, making "count would drop to
    // zero" structurally unreachable through this endpoint alone in the normal case. The guard still
    // exists deliberately as defense-in-depth (a caller whose own account was deactivated moments earlier
    // but whose access token is still valid within its window, or any future code path that reaches these
    // handlers without going through this exact authorization gate), which is exactly why it is proven at
    // the handler level, where that scenario can be constructed directly instead of assumed away.
    //
    // What IS meaningfully provable at the HTTP level is the safe case: deactivating one of two
    // administrators, leaving the other, must succeed - the guard must never block a change that still
    // leaves the tenant with an administrator. Uses a fresh, test-local second admin (promoted from a
    // freshly invited user) rather than the shared AcmeSysAdmin seed fixture, which other tests in this
    // class also depend on remaining Active/SysAdmin.
    [Fact]
    public async Task DeactivateUser_OneOfTwoAdministrators_Succeeds()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var secondAdmin = await CreateActiveUserAsync(client, "Second", "Admin");
        await client.PostAsJsonAsync($"/api/users/{secondAdmin.UserId}/roles", new { role = "TenantAdmin" });

        var response = await client.DeleteAsync($"/api/users/{secondAdmin.UserId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeactivatedUser_CannotLogIn()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Will", "BeDeactivated");
        await client.DeleteAsync($"/api/users/{created.UserId}");

        using var anonymousClient = _factory.CreateClient();
        var loginResponse = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email = created.Email, password = created.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
    }

    [Fact]
    public async Task ReactivateUser_AnInactiveUser_SetsStatusBackToActive()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "To", "Reactivate");
        await client.DeleteAsync($"/api/users/{created.UserId}");

        var response = await client.PostAsync($"/api/users/{created.UserId}/reactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserDetailResponse>(JsonOptions);
        Assert.Equal(0, body!.Status); // UserStatus.Active
    }

    [Fact]
    public async Task ReactivateUser_AnAlreadyActiveUser_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Already", "Active");

        var response = await client.PostAsync($"/api/users/{created.UserId}/reactivate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.StatusConflict", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task ReactivatedUser_CanLogInAgain()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Will", "BeReactivated");
        await client.DeleteAsync($"/api/users/{created.UserId}");
        await client.PostAsync($"/api/users/{created.UserId}/reactivate", null);

        using var anonymousClient = _factory.CreateClient();
        var loginResponse = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email = created.Email, password = created.Password });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    // --- Roles ---

    [Fact]
    public async Task AssignUserRole_AsTenantAdmin_AddsTheRole()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Role", "Recipient");

        var response = await client.PostAsJsonAsync($"/api/users/{created.UserId}/roles", new { role = "Approver" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AssignRoleResponse>(JsonOptions);
        Assert.Contains("Approver", body!.Roles);
    }

    [Fact]
    public async Task AssignUserRole_ARoleAlreadyHeld_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}/roles", new { role = "Approver" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.RoleConflict", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task AssignUserRole_WithSysAdmin_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync($"/api/users/{TestDataSeeder.AcmeMemberUserId}/roles", new { role = "SysAdmin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssignUserRole_AsMember_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}/roles", new { role = "TenantAdmin" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveUserRole_AsTenantAdmin_RemovesTheRole()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Role", "Loser");
        await client.PostAsJsonAsync($"/api/users/{created.UserId}/roles", new { role = "Approver" });

        var response = await client.DeleteAsync($"/api/users/{created.UserId}/roles/Approver");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveUserRole_ARoleNotHeld_ReturnsNotFound()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeMemberUserId}/roles/TenantAdmin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("User.RoleNotAssigned", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task RemoveUserRole_CallerRemovingTheirOwnAdministrativeRole_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeAdminUserId}/roles/TenantAdmin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.SelfLockout", await ReadErrorCodeAsync(response));
    }

    // Same structural point as DeactivateUser's own comment above: any HTTP caller who successfully
    // reaches this authorized endpoint necessarily counts as an active administrator themselves (their
    // JWT role claim reflects their own current DB row in the ordinary case), and the caller can never
    // be the target (self-lockout, checked above, rejects that first) - so a genuinely reachable "the
    // active-administrator count would drop to zero" via two distinct, successfully-authorized HTTP
    // callers is not constructible. The guard is proven directly at the handler level instead (see
    // RemoveUserRoleCommandHandlerTests), where the scenario it defends against (e.g. a caller whose own
    // admin status was revoked moments before their still-valid access token was used) can be
    // constructed without that structural constraint. What IS provable here is the safe case below.
    [Fact]
    public async Task RemoveUserRole_OneOfTwoAdministratorsOwnAdminRole_Succeeds()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var secondAdmin = await CreateActiveUserAsync(client, "Second", "Admin");
        await client.PostAsJsonAsync($"/api/users/{secondAdmin.UserId}/roles", new { role = "TenantAdmin" });

        var response = await client.DeleteAsync($"/api/users/{secondAdmin.UserId}/roles/TenantAdmin");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // AcmeApproverUserId is seeded (TestDataSeeder) holding the global Approver role AND a real
    // ResourceApprover assignment on AcmeApprovalRequiredResourceId - exactly the state this guard exists
    // to catch, with no extra setup needed.
    [Fact]
    public async Task RemoveUserRole_ApproverRoleWhileResourceApproverAssignmentsExist_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeApproverUserId}/roles/Approver");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("User.ApproverAssignmentsExist", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task RemoveUserRole_WithSysAdmin_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.DeleteAsync($"/api/users/{TestDataSeeder.AcmeSysAdminUserId}/roles/SysAdmin");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Documents the accepted tradeoff explicitly, rather than only asserting it in prose - see
    // docs/open-questions.md's "Dynamic Authorization" entry. An access token issued BEFORE a role
    // change keeps working with its OLD roles until it naturally expires; there is no mechanism to
    // revoke it early.
    [Fact]
    public async Task AssigningARole_DoesNotChangeAnAlreadyIssuedAccessTokensClaims()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);
        var created = await CreateActiveUserAsync(client, "Stale", "Claims");
        using var subjectClient = _factory.CreateClient();
        var loginResponse = await subjectClient.PostAsJsonAsync("/api/auth/login", new { email = created.Email, password = created.Password });
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokensResponse>(JsonOptions);
        subjectClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens!.AccessToken);

        await client.PostAsJsonAsync($"/api/users/{created.UserId}/roles", new { role = "TenantAdmin" });

        // Still carries only the roles from login time - the already-issued token has no TenantAdmin
        // claim, so an admin-only action must still be forbidden with this exact token.
        var attemptAdminAction = await subjectClient.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, attemptAdminAction.StatusCode);
    }

    // --- Helpers ---

    private async Task<(Guid UserId, string Email, string Password)> CreateActiveUserAsync(HttpClient adminClient, string firstName, string lastName)
    {
        var email = $"{Guid.NewGuid()}@acme.integration-test";
        var invite = await adminClient.PostAsJsonAsync("/api/users/invitations", new { email, firstName, lastName, roles = new[] { "Member" } });
        var invitation = (await invite.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOptions))!;
        const string password = "a-New-Passw0rd!";
        using var anonymousClient = _factory.CreateClient();
        await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token = invitation.InvitationToken, password });
        return (invitation.UserId, email, password);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<TokensResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record TokensResponse(string AccessToken);

    private sealed record InviteUserResponse(Guid UserId, string Email, string InvitationToken, DateTimeOffset InvitationExpiresAtUtc);

    private sealed record UserDetailResponse(Guid Id, string FirstName, string LastName, string Email, int Status, List<string> Roles);

    private sealed record AssignRoleResponse(Guid UserId, List<string> Roles);
}
