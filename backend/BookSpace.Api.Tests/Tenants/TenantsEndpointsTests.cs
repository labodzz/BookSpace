using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookSpace.Api.Tests.Tenants;

// BookSpace tenant onboarding AND lifecycle - see docs/tenant-onboarding.md. Exercises the real HTTP
// pipeline end to end: SysAdmin request -> TenantsController -> CreateTenantCommandHandler -> real
// outbox row -> (this test reads the queued payload directly from the database, the same way a human
// operator never can, but the only way to recover the invitation link without a running background
// worker/real email inbox) -> POST /api/auth/accept-invitation -> POST /api/auth/login -> GET /api/users
// proving tenant isolation. The lifecycle section further below (list/detail/rename/suspend/reactivate/
// delete/replacement-admin invitation) is SysAdmin-only platform administration, never a tenant's own
// business function - TenantAdmin never gets access, not even to its own tenant.
public sealed class TenantsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public TenantsEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateTenant_AsSysAdmin_ReturnsCreatedWithTheNewTenantAndAdminButNeverATokenOrUrl()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";

        var response = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Acme API Co {Guid.NewGuid():N}", defaultTimeZoneId = "Europe/Zagreb",
            adminFirstName = "Ana", adminLastName = "Anic", adminEmail,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var bodyText = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<CreateTenantResponseBody>(bodyText, JsonOptions);
        Assert.NotEqual(Guid.Empty, body!.TenantId);
        Assert.NotEqual(Guid.Empty, body.AdminUserId);
        Assert.Equal(adminEmail, body.AdminEmail);
        // Never exposes the raw invitation token or URL in the response, unlike InviteUserCommandHandler's
        // older response shape - see CreateTenantResponse's own doc comment.
        Assert.DoesNotContain("token", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accept-invitation", bodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(TestDataSeeder.AcmeAdminEmail))]
    [InlineData(nameof(TestDataSeeder.AcmeMemberEmail))]
    [InlineData(nameof(TestDataSeeder.AcmeApproverEmail))]
    public async Task CreateTenant_AsAnyoneOtherThanSysAdmin_ReturnsForbidden(string emailFieldName)
    {
        var email = emailFieldName switch
        {
            nameof(TestDataSeeder.AcmeAdminEmail) => TestDataSeeder.AcmeAdminEmail,
            nameof(TestDataSeeder.AcmeMemberEmail) => TestDataSeeder.AcmeMemberEmail,
            _ => TestDataSeeder.AcmeApproverEmail,
        };
        using var client = await AuthenticatedClientAsync(email);

        var response = await client.PostAsJsonAsync("/api/tenants", ValidCreateTenantRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tenants", ValidCreateTenantRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_WithATenantNameThatAlreadyExists_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = "Acme Coworking", // seeded by TestDataSeeder
            defaultTimeZoneId = "UTC", adminFirstName = "Ana", adminLastName = "Anic", adminEmail = $"ana-{Guid.NewGuid():N}@acme.test",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_WithAnAdminEmailAlreadyBelongingToAnExistingUser_ReturnsConflict()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await client.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Acme API Co {Guid.NewGuid():N}", defaultTimeZoneId = "UTC",
            adminFirstName = "Ana", adminLastName = "Anic", adminEmail = TestDataSeeder.AcmeMemberEmail, // already exists
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_WithMissingRequiredFields_ReturnsBadRequest()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await client.PostAsJsonAsync("/api/tenants", new { name = "", defaultTimeZoneId = "UTC", adminFirstName = "", adminLastName = "", adminEmail = "not-an-email" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheFullOnboardingChain_CreateQueueAcceptAndLogIn_WorksThroughTheRealHttpPipeline()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var createResponse = await sysAdminClient.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Acme Full Chain {Guid.NewGuid():N}", defaultTimeZoneId = "UTC", adminFirstName = "Ana", adminLastName = "Anic", adminEmail,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Cannot log in yet - still Invited.
        using var anonymousClient = _factory.CreateClient();
        var premetureLogin = await anonymousClient.PostAsJsonAsync("/api/auth/login", new { email = adminEmail, password = "whatever-At-All1" });
        Assert.Equal(HttpStatusCode.Unauthorized, premetureLogin.StatusCode);

        // The invitation link is only ever recoverable from the queued outbox payload (there is no running
        // background worker/real inbox in this test host) - the same place the real email sender reads it from.
        var invitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail);
        var token = new Uri(invitationUrl).Query.Split("token=")[1];

        var acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token, password = TestDataSeeder.Password });
        Assert.Equal(HttpStatusCode.NoContent, acceptResponse.StatusCode);

        // The SAME token can never accept again.
        var secondAccept = await anonymousClient.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token, password = "Different-Passw0rd!" });
        Assert.Equal(HttpStatusCode.Unauthorized, secondAccept.StatusCode);

        // Login now succeeds (with the password actually set via accept-invitation above), and the admin is
        // scoped to their OWN new tenant - never Acme's own seeded data (the existing GET /api/users
        // tenant-isolation guarantee UsersEndpointsTests already proves generically; this proves it
        // specifically holds for a freshly onboarded tenant too).
        using var newAdminClient = await AuthenticatedClientAsync(adminEmail);
        var usersResponse = await newAdminClient.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        var usersBody = await usersResponse.Content.ReadFromJsonAsync<PagedResponse<UserSummaryResponse>>(JsonOptions);
        Assert.Single(usersBody!.Items);
        Assert.Equal(adminEmail, usersBody.Items[0].Email);
        Assert.Contains("TenantAdmin", usersBody.Items[0].Roles);
    }

    [Fact]
    public async Task ResendInvitation_AsSysAdmin_RevokesTheOldTokenAndIssuesAWorkingNewOne()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var createResponse = await sysAdminClient.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Acme Resend Co {Guid.NewGuid():N}", defaultTimeZoneId = "UTC", adminFirstName = "Ana", adminLastName = "Anic", adminEmail,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateTenantResponseBody>(JsonOptions);
        var firstInvitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail);
        var firstToken = new Uri(firstInvitationUrl).Query.Split("token=")[1];

        var resendResponse = await sysAdminClient.PostAsync($"/api/tenants/{created!.TenantId}/users/{created.AdminUserId}/resend-invitation", content: null);
        Assert.Equal(HttpStatusCode.OK, resendResponse.StatusCode);

        var secondInvitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail, excluding: firstInvitationUrl);
        var secondToken = new Uri(secondInvitationUrl).Query.Split("token=")[1];

        using var anonymousClient = _factory.CreateClient();
        var oldTokenAccept = await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token = firstToken, password = "Some-Passw0rd!" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenAccept.StatusCode);

        var newTokenAccept = await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token = secondToken, password = "Some-Passw0rd!" });
        Assert.Equal(HttpStatusCode.NoContent, newTokenAccept.StatusCode);
    }

    [Fact]
    public async Task ResendInvitation_ForAnAdminWhoAlreadyAccepted_ReturnsConflict()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var createResponse = await sysAdminClient.PostAsJsonAsync("/api/tenants", new
        {
            name = $"Acme Already Active Co {Guid.NewGuid():N}", defaultTimeZoneId = "UTC", adminFirstName = "Ana", adminLastName = "Anic", adminEmail,
        });
        var created = await createResponse.Content.ReadFromJsonAsync<CreateTenantResponseBody>(JsonOptions);
        var invitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail);
        var token = new Uri(invitationUrl).Query.Split("token=")[1];
        using var anonymousClient = _factory.CreateClient();
        await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token, password = "Correct-Horse-Battery-9!" });

        var resendResponse = await sysAdminClient.PostAsync($"/api/tenants/{created!.TenantId}/users/{created.AdminUserId}/resend-invitation", content: null);

        Assert.Equal(HttpStatusCode.Conflict, resendResponse.StatusCode);
    }

    [Fact]
    public async Task ResendInvitation_AsNonSysAdmin_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsync($"/api/tenants/{Guid.NewGuid()}/users/{Guid.NewGuid()}/resend-invitation", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResendInvitation_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/tenants/{Guid.NewGuid()}/users/{Guid.NewGuid()}/resend-invitation", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Tenant lifecycle: list/detail/rename/suspend/reactivate/delete/replacement-admin invitation -
    // see docs/tenant-onboarding.md, "Tenant lifecycle". ---

    [Fact]
    public async Task GetTenants_AsSysAdmin_ReturnsOkWithAggregatesAndTheActiveTenantAdminEmail()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await sysAdminClient.GetAsync("/api/tenants?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PagedResponse<TenantSummaryResponseBody>>(JsonOptions);
        var acme = Assert.Single(body!.Items, tenant => tenant.Id == TestDataSeeder.AcmeTenantId);
        Assert.True(acme.HasActiveTenantAdmin);
        Assert.Contains(TestDataSeeder.AcmeAdminEmail, acme.ActiveTenantAdminEmails);
        Assert.True(acme.TotalUserCount >= 1);
        // Never leaks booking/resource details - only platform aggregates and counts (see
        // TenantSummaryResponse's own doc comment) - confirmed by the response shape itself containing
        // no such field, not merely by omission here.
    }

    [Fact]
    public async Task GetTenants_AsNonSysAdmin_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.GetAsync("/api/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTenant_AsSysAdmin_ReturnsDetailForAnyTenant()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await sysAdminClient.GetAsync($"/api/tenants/{TestDataSeeder.AcmeTenantId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantDetailResponseBody>(JsonOptions);
        Assert.Equal(TestDataSeeder.AcmeTenantId, body!.Id);
        Assert.Equal("Acme Coworking", body.Name);
        Assert.True(body.HasActiveTenantAdmin);
    }

    [Fact]
    public async Task GetTenant_WithUnknownId_ReturnsNotFound()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);

        var response = await sysAdminClient.GetAsync($"/api/tenants/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTenant_AsSysAdmin_RenamesTheTenant()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var created = await CreateTenantAsync(sysAdminClient, "Acme Rename Target");
        var newName = $"Renamed Co {Guid.NewGuid():N}";

        var response = await sysAdminClient.PutAsJsonAsync($"/api/tenants/{created.TenantId}", new { name = newName });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantStatusResponseBody>(JsonOptions);
        Assert.Equal(newName, body!.Name);
    }

    [Fact]
    public async Task UpdateTenant_ToAnAlreadyUsedName_ReturnsConflict()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var created = await CreateTenantAsync(sysAdminClient, "Acme Rename Collision Target");

        var response = await sysAdminClient.PutAsJsonAsync($"/api/tenants/{created.TenantId}", new { name = "Acme Coworking" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SuspendThenReactivate_BlocksThenRestoresTheTenantAdminsLogin()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var created = await CreateTenantAsync(sysAdminClient, $"Acme Suspend Co {Guid.NewGuid():N}", adminEmail);
        await AcceptQueuedInvitationAsync(adminEmail, "Correct-Horse-Battery-9!");

        var suspendResponse = await sysAdminClient.PostAsync($"/api/tenants/{created.TenantId}/suspend", content: null);
        Assert.Equal(HttpStatusCode.OK, suspendResponse.StatusCode);
        var suspendBody = await suspendResponse.Content.ReadFromJsonAsync<TenantStatusResponseBody>(JsonOptions);
        Assert.Equal(1, suspendBody!.Status); // TenantStatus.Suspended

        using var anonymousClient = _factory.CreateClient();
        var blockedLogin = await anonymousClient.PostAsJsonAsync(
            "/api/auth/login", new { email = adminEmail, password = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, blockedLogin.StatusCode);

        var reactivateResponse = await sysAdminClient.PostAsync($"/api/tenants/{created.TenantId}/reactivate", content: null);
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);

        var restoredLogin = await anonymousClient.PostAsJsonAsync(
            "/api/auth/login", new { email = adminEmail, password = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.OK, restoredLogin.StatusCode);
    }

    [Fact]
    public async Task SuspendTenant_WhenAlreadySuspended_ReturnsConflict()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var created = await CreateTenantAsync(sysAdminClient, $"Acme Double Suspend Co {Guid.NewGuid():N}");
        await sysAdminClient.PostAsync($"/api/tenants/{created.TenantId}/suspend", content: null);

        var response = await sysAdminClient.PostAsync($"/api/tenants/{created.TenantId}/suspend", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ReactivateTenant_WhenAlreadyActive_ReturnsConflict()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var created = await CreateTenantAsync(sysAdminClient, $"Acme Already Active Reactivate Co {Guid.NewGuid():N}");

        var response = await sysAdminClient.PostAsync($"/api/tenants/{created.TenantId}/reactivate", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTenant_WithWrongConfirmationName_ReturnsConflictAndDoesNotDelete()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var name = $"Acme Delete Mismatch Co {Guid.NewGuid():N}";
        var created = await CreateTenantAsync(sysAdminClient, name);

        var response = await sysAdminClient.PostAsJsonAsync($"/api/tenants/{created.TenantId}/delete", new { confirmName = "Not The Right Name" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var getResponse = await sysAdminClient.GetAsync($"/api/tenants/{created.TenantId}");
        var body = await getResponse.Content.ReadFromJsonAsync<TenantDetailResponseBody>(JsonOptions);
        Assert.Equal(0, body!.Status); // TenantStatus.Active - untouched
    }

    [Fact]
    public async Task DeleteTenant_WithTheExactCurrentName_SoftDeletesAndBlocksItsAdminsLogin()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var name = $"Acme Delete Co {Guid.NewGuid():N}";
        var created = await CreateTenantAsync(sysAdminClient, name, adminEmail);
        await AcceptQueuedInvitationAsync(adminEmail, "Correct-Horse-Battery-9!");

        var response = await sysAdminClient.PostAsJsonAsync($"/api/tenants/{created.TenantId}/delete", new { confirmName = name });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantStatusResponseBody>(JsonOptions);
        Assert.Equal(2, body!.Status); // TenantStatus.Deleted

        using var anonymousClient = _factory.CreateClient();
        var blockedLogin = await anonymousClient.PostAsJsonAsync(
            "/api/auth/login", new { email = adminEmail, password = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, blockedLogin.StatusCode);
    }

    [Fact]
    public async Task DeleteTenant_WhenAlreadyDeleted_ReturnsConflict()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var name = $"Acme Double Delete Co {Guid.NewGuid():N}";
        var created = await CreateTenantAsync(sysAdminClient, name);
        await sysAdminClient.PostAsJsonAsync($"/api/tenants/{created.TenantId}/delete", new { confirmName = name });

        var response = await sysAdminClient.PostAsJsonAsync($"/api/tenants/{created.TenantId}/delete", new { confirmName = name });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task InviteReplacementTenantAdmin_QueuesAWorkingInvitationForTheNewPerson()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var created = await CreateTenantAsync(sysAdminClient, $"Acme Replacement Co {Guid.NewGuid():N}");
        var replacementEmail = $"replacement-{Guid.NewGuid():N}@acme.test";

        var response = await sysAdminClient.PostAsJsonAsync($"/api/tenants/{created.TenantId}/admin-invitations", new
        {
            firstName = "Belma", lastName = "Novak", email = replacementEmail,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitationUrl = await ReadQueuedInvitationUrlAsync(replacementEmail);
        var token = new Uri(invitationUrl).Query.Split("token=")[1];
        using var anonymousClient = _factory.CreateClient();
        var acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token, password = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.NoContent, acceptResponse.StatusCode);

        // Not AuthenticatedClientAsync(replacementEmail) - that helper always logs in with
        // TestDataSeeder.Password (the shared seed password), but this account's password was just set
        // to a different value via accept-invitation above.
        using var newAdminClient = await AuthenticatedClientAsync(replacementEmail, "Correct-Horse-Battery-9!");
        var meResponse = await newAdminClient.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task InviteReplacementTenantAdmin_AsNonSysAdmin_ReturnsForbidden()
    {
        using var client = await AuthenticatedClientAsync(TestDataSeeder.AcmeAdminEmail);

        var response = await client.PostAsJsonAsync($"/api/tenants/{TestDataSeeder.AcmeTenantId}/admin-invitations", new
        {
            firstName = "Belma", lastName = "Novak", email = $"nope-{Guid.NewGuid():N}@acme.test",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokeAdminInvitation_RevokesThePendingInvitationSoItCanNoLongerBeAccepted()
    {
        using var sysAdminClient = await AuthenticatedClientAsync(TestDataSeeder.GlobalSysAdminEmail);
        var adminEmail = $"ana-{Guid.NewGuid():N}@acme.test";
        var created = await CreateTenantAsync(sysAdminClient, $"Acme Revoke Co {Guid.NewGuid():N}", adminEmail);
        var invitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail);
        var token = new Uri(invitationUrl).Query.Split("token=")[1];

        var revokeResponse = await sysAdminClient.DeleteAsync($"/api/tenants/{created.TenantId}/users/{created.AdminUserId}/invitation");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        using var anonymousClient = _factory.CreateClient();
        var acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/api/auth/accept-invitation", new { token, password = "Correct-Horse-Battery-9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, acceptResponse.StatusCode);
    }

    // Reuses the exact create-tenant-then-read-the-queued-outbox-payload flow every test above already
    // uses, with an optional override for the first admin's email - see ReadQueuedInvitationUrlAsync.
    private async Task<CreateTenantResponseBody> CreateTenantAsync(HttpClient sysAdminClient, string tenantName, string? adminEmail = null)
    {
        var response = await sysAdminClient.PostAsJsonAsync("/api/tenants", new
        {
            name = tenantName, defaultTimeZoneId = "UTC", adminFirstName = "Ana", adminLastName = "Anic",
            adminEmail = adminEmail ?? $"ana-{Guid.NewGuid():N}@acme.test",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateTenantResponseBody>(JsonOptions))!;
    }

    private async Task AcceptQueuedInvitationAsync(string adminEmail, string password)
    {
        var invitationUrl = await ReadQueuedInvitationUrlAsync(adminEmail);
        var token = new Uri(invitationUrl).Query.Split("token=")[1];
        using var anonymousClient = _factory.CreateClient();
        var response = await anonymousClient.PostAsJsonAsync("/api/auth/accept-invitation", new { token, password });
        response.EnsureSuccessStatusCode();
    }

    private static object ValidCreateTenantRequest() => new
    {
        name = $"Acme API Co {Guid.NewGuid():N}", defaultTimeZoneId = "UTC",
        adminFirstName = "Ana", adminLastName = "Anic", adminEmail = $"ana-{Guid.NewGuid():N}@acme.test",
    };

    // Reads the invitation link directly out of the queued NotificationOutboxItem's payload - the only way
    // to recover it in a test host with no running background worker and no real inbox (see
    // docs/tenant-onboarding.md, "Where the raw token lives, and for how long").
    private async Task<string> ReadQueuedInvitationUrlAsync(string adminEmail, string? excluding = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var user = await dbContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == adminEmail);
        var items = await dbContext.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.RecipientUserId == user.Id && item.NotificationType == "Tenant.AdminInvitation")
            .Select(item => item.PayloadJson)
            .ToListAsync();

        foreach (var payloadJson in items)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var url = document.RootElement.GetProperty("InvitationUrl").GetString()!;
            if (url != excluding)
            {
                return url;
            }
        }

        throw new InvalidOperationException("No matching queued invitation payload found.");
    }

    // password defaults to the shared seed password - every seeded fixture user has it. A user created
    // through accept-invitation during a test (e.g. a brand-new tenant's admin) has whatever password
    // that test's own accept-invitation call set instead, and must pass it explicitly.
    private async Task<HttpClient> AuthenticatedClientAsync(string email, string password = TestDataSeeder.Password)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record LoginResponse(string AccessToken);

    private sealed record CreateTenantResponseBody(Guid TenantId, string TenantName, Guid AdminUserId, string AdminEmail);

    private sealed record UserSummaryResponse(Guid Id, string FirstName, string LastName, string Email, Guid TenantId, int Status, List<string> Roles);

    private sealed record PagedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount);

    // Status is the raw underlying int (TenantStatus has no JsonStringEnumConverter registered, the same
    // convention user.mappers.ts/resource.mappers.ts document on the frontend side) - Active=0,
    // Suspended=1, Deleted=2.
    private sealed record TenantSummaryResponseBody(
        Guid Id, string Name, int Status, DateTimeOffset CreatedAtUtc, int TotalUserCount, int ActiveUserCount,
        int InvitedUserCount, int InactiveUserCount, List<string> ActiveTenantAdminEmails, bool HasActiveTenantAdmin);

    private sealed record TenantDetailResponseBody(
        Guid Id, string Name, string DefaultTimeZoneId, int Status, DateTimeOffset CreatedAtUtc, int TotalUserCount,
        int ActiveUserCount, int InvitedUserCount, int InactiveUserCount, List<string> ActiveTenantAdminEmails,
        bool HasActiveTenantAdmin, List<PendingTenantAdminContactBody> PendingTenantAdminInvitations);

    private sealed record PendingTenantAdminContactBody(Guid UserId, string Email);

    private sealed record TenantStatusResponseBody(Guid Id, string Name, int Status);
}
