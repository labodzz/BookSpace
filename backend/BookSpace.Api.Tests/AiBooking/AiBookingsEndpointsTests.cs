using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.AiBooking;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookSpace.Api.Tests.AiBooking;

// Full-stack coverage (real HTTP pipeline, real SQLite-backed handlers, only IBookingIntentInterpreter
// faked - see FakeBookingIntentInterpreter) for the task's own acceptance criteria that need more than a
// mocked-repository unit test to mean anything:
//   #2 - confirming a proposal uses the REAL POST /api/bookings (BookingsController/CreateBookingCommandHandler),
//        never a parallel path;
//   #4 - a slot taken after the proposal but before confirmation is rejected by the SAME concurrency/
//        validation path an ordinary manual booking would hit;
//   #5 - AI cannot resolve another tenant's resource, proven against the REAL tenant-filtered repositories
//        and TWO REAL seeded tenants (Acme/Globex), not just a mock that happens to return an empty list;
//   #11 - a RequiresApproval resource's Pending/approval behavior is unchanged when reached via this flow.
// AiBooking:Enabled is left at its real default (false, from appsettings.json - CustomWebApplicationFactory
// sets no override) for every test EXCEPT the ones that explicitly need it on via WithAiBookingEnabled -
// this itself proves "operates normally without an LLM API key" for the disabled case.
public sealed class AiBookingsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CustomWebApplicationFactory _factory;

    public AiBookingsEndpointsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static DateTimeOffset AnchorPlusHours(int hours) => TestDataSeeder.AvailabilityAnchorUtc.AddHours(hours);

    [Fact]
    public async Task Propose_WhenAiBookingIsDisabled_ReturnsOkWithDisabledOutcomeAndNeverThrows()
    {
        // The REAL, unmodified factory - AiBooking:Enabled is whatever appsettings.json/environment
        // actually says (false, since CustomWebApplicationFactory sets no AiBooking__* override) - this is
        // exactly the "app starts and operates normally without an LLM API key" requirement, proven
        // end-to-end rather than assumed.
        using var client = await AuthenticatedClientAsync(_factory, TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book a meeting room tomorrow from 2 to 4 PM." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions);
        Assert.Equal(ProposeBookingOutcome.Disabled, body!.Outcome);
        Assert.Null(body.Candidate);
    }

    [Fact]
    public async Task Propose_AsTheGlobalSysAdmin_ReturnsConflict()
    {
        var fake = new FakeBookingIntentInterpreter();
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.GlobalSysAdminEmail);

        var response = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book a meeting room tomorrow." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Common.TenantMembershipRequired", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Propose_WithTextLongerThanTheMaximum_ReturnsBadRequest()
    {
        var fake = new FakeBookingIntentInterpreter();
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync(
            "/api/ai-bookings/propose", new { text = new string('a', ProposeBookingCommandRequestValidator.MaxTextLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The task's own #1 and #2 acceptance criteria, end to end: a valid request produces a concrete
    // candidate with NOTHING created yet, and explicit confirmation - sending exactly the candidate's own
    // fields to the EXISTING POST /api/bookings - is what actually creates the real, Confirmed booking.
    [Fact]
    public async Task Propose_WithAValidRequest_ReturnsACandidate_AndConfirmingItCreatesARealConfirmedBooking()
    {
        var start = AnchorPlusHours(100);
        var fake = new FakeBookingIntentInterpreter
        {
            // Acme has TWO "Meeting Room"-type resources (Integration Test Room, Approval Required Room -
            // see TestDataSeeder) - a type hint alone is deliberately ambiguous between them (see the
            // NeedsClarification test below), so this test also names the specific one it means.
            NextResponse = ValidIntent(start, quantity: 1, resourceTypeHint: "meeting room", resourceNameHint: "Integration Test Room"),
        };
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var proposeResponse = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book the meeting room." });
        Assert.Equal(HttpStatusCode.OK, proposeResponse.StatusCode);
        var proposal = await proposeResponse.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions);
        Assert.Equal(ProposeBookingOutcome.Proposed, proposal!.Outcome);
        Assert.NotNull(proposal.Candidate);
        Assert.Equal(TestDataSeeder.AcmeResourceId, proposal.Candidate!.ResourceId);

        // Nothing was created by the proposal itself - confirmed by listing this member's own bookings
        // for the exact candidate window before ever calling POST /api/bookings.
        var beforeConfirm = await client.GetFromJsonAsync<PagedResultDto<BookingDto>>(
            $"/api/bookings?fromUtc={Uri.EscapeDataString(proposal.Candidate.StartUtc.ToString("O"))}&toUtc={Uri.EscapeDataString(proposal.Candidate.EndUtc.ToString("O"))}",
            JsonOptions);
        Assert.Empty(beforeConfirm!.Items);

        // Confirmation: the real, unmodified POST /api/bookings, with exactly the candidate's own fields -
        // there is no other "confirm" endpoint anywhere in this feature (see AiBookingsController's own
        // doc comment).
        var confirmResponse = await client.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = proposal.Candidate.ResourceId,
            startUtc = proposal.Candidate.StartUtc,
            endUtc = proposal.Candidate.EndUtc,
            quantity = proposal.Candidate.Quantity,
        });

        Assert.Equal(HttpStatusCode.Created, confirmResponse.StatusCode);
        var created = await confirmResponse.Content.ReadFromJsonAsync<BookingDto>(JsonOptions);
        Assert.Equal(BookingStatus.Confirmed, created!.Status);
    }

    // The task's own #4 acceptance criterion: once someone else's booking has consumed the exact slot a
    // proposal pointed at, confirming it must be rejected by the REAL CreateBookingCommandHandler's own
    // capacity re-check - never silently waved through because "the AI already said it was available".
    [Fact]
    public async Task Propose_ThenTheSlotFillsUpBeforeConfirmation_ConfirmingIsRejectedByTheRealCapacityCheck()
    {
        var start = AnchorPlusHours(150);
        // Full configured capacity (8) for TestDataSeeder.AcmeResourceId - ANY other booking landing in
        // this exact window afterward must make confirmation fail.
        var fake = new FakeBookingIntentInterpreter
        {
            NextResponse = ValidIntent(start, quantity: 8, resourceTypeHint: "meeting room", resourceNameHint: "Integration Test Room"),
        };
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var proposeResponse = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book the whole meeting room." });
        var proposal = (await proposeResponse.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions))!;
        Assert.Equal(ProposeBookingOutcome.Proposed, proposal.Outcome);

        // Someone else takes one unit of the exact same window in the meantime.
        using var otherClient = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeAdminEmail);
        var interveningBooking = await otherClient.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = proposal.Candidate!.ResourceId, startUtc = proposal.Candidate.StartUtc, endUtc = proposal.Candidate.EndUtc, quantity = 1,
        });
        Assert.Equal(HttpStatusCode.Created, interveningBooking.StatusCode);

        var confirmResponse = await client.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = proposal.Candidate.ResourceId, startUtc = proposal.Candidate.StartUtc, endUtc = proposal.Candidate.EndUtc,
            quantity = proposal.Candidate.Quantity,
        });

        Assert.Equal(HttpStatusCode.Conflict, confirmResponse.StatusCode);
        Assert.Equal("Booking.CapacityExceeded", await ReadErrorCodeAsync(confirmResponse));
    }

    // #11: a resource configured with RequiresApproval must still go through the EXISTING Pending/approval
    // flow when reached via this feature - the AI never bypasses it, never auto-approves.
    [Fact]
    public async Task Propose_ForAResourceThatRequiresApproval_TheConfirmedBookingIsPendingNotConfirmed()
    {
        var start = AnchorPlusHours(200);
        var fake = new FakeBookingIntentInterpreter
        {
            NextResponse = ValidIntent(start, quantity: 1, resourceTypeHint: "meeting room", resourceNameHint: "Approval Required Room"),
        };
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var proposeResponse = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book the approval-required room." });
        var proposal = (await proposeResponse.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions))!;
        Assert.Equal(ProposeBookingOutcome.Proposed, proposal.Outcome);
        Assert.Equal(TestDataSeeder.AcmeApprovalRequiredResourceId, proposal.Candidate!.ResourceId);
        Assert.True(proposal.Candidate.RequiresApproval);

        var confirmResponse = await client.PostAsJsonAsync("/api/bookings", new
        {
            resourceId = proposal.Candidate.ResourceId, startUtc = proposal.Candidate.StartUtc, endUtc = proposal.Candidate.EndUtc,
            quantity = proposal.Candidate.Quantity,
        });

        Assert.Equal(HttpStatusCode.Created, confirmResponse.StatusCode);
        var created = await confirmResponse.Content.ReadFromJsonAsync<BookingDto>(JsonOptions);
        Assert.Equal(BookingStatus.Pending, created!.Status);
    }

    // #5, the full-stack version: an Acme member asking for a resource that only exists for Globex must
    // never resolve to it - proven against the REAL tenant-filtered IResourceRepository and two REAL
    // seeded tenants, not a mock that merely happens to return an empty list.
    [Fact]
    public async Task Propose_WhenTheHintOnlyMatchesAnotherTenantsResource_ReturnsNoSuitableResource()
    {
        var start = AnchorPlusHours(250);
        var fake = new FakeBookingIntentInterpreter
        {
            NextResponse = ValidIntent(start, quantity: 1, resourceTypeHint: "meeting room", resourceNameHint: "Globex Only Room"),
        };
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book the Globex Only Room." });

        var body = await response.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions);
        Assert.Equal(ProposeBookingOutcome.NoSuitableResource, body!.Outcome);
        Assert.Null(body.Candidate);
    }

    // Acme genuinely has two "Meeting Room"-type resources (see TestDataSeeder) - a type hint with no name
    // and no party size narrow enough to pick between them must ask, not silently guess one.
    [Fact]
    public async Task Propose_WhenTheTypeHintMatchesMultipleRealResources_ReturnsNeedsClarificationNamingBoth()
    {
        var start = AnchorPlusHours(300);
        var fake = new FakeBookingIntentInterpreter { NextResponse = ValidIntent(start, quantity: 1, resourceTypeHint: "meeting room") };
        await using var factory = WithAiBookingEnabled(fake);
        using var client = await AuthenticatedClientAsync(factory, TestDataSeeder.AcmeMemberEmail);

        var response = await client.PostAsJsonAsync("/api/ai-bookings/propose", new { text = "Book a meeting room." });

        var body = await response.Content.ReadFromJsonAsync<ProposeBookingResponseDto>(JsonOptions);
        Assert.Equal(ProposeBookingOutcome.NeedsClarification, body!.Outcome);
        Assert.Contains("Integration Test Room", body.Message);
        Assert.Contains("Approval Required Room", body.Message);
    }

    private static LlmBookingIntent ValidIntent(
        DateTimeOffset startUtc, int? quantity = null, string? resourceTypeHint = null, string? resourceNameHint = null) => new(
        "en", "ok", null, resourceTypeHint, resourceNameHint, null, quantity,
        DateOnly.FromDateTime(startUtc.UtcDateTime).ToString("yyyy-MM-dd"), false,
        TimeOnly.FromDateTime(startUtc.UtcDateTime).ToString("HH:mm"),
        TimeOnly.FromDateTime(startUtc.AddHours(1).UtcDateTime).ToString("HH:mm"), null);

    private WebApplicationFactory<Program> WithAiBookingEnabled(FakeBookingIntentInterpreter fake) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiBooking:Enabled"] = "true",
                ["AiBooking:ApiKey"] = "test-key",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBookingIntentInterpreter>();
                services.AddSingleton<IBookingIntentInterpreter>(fake);
            });
        });

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() : null;
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> factory, string email)
    {
        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestDataSeeder.Password });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);
        return client;
    }

    private sealed record LoginResponseDto(string AccessToken);

    private sealed record ProposeBookingResponseDto(ProposeBookingOutcome Outcome, string Language, string? Message, ProposeBookingCandidateDto? Candidate);

    private sealed record ProposeBookingCandidateDto(
        Guid ResourceId, string ResourceName, string TimeZoneId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
        int Quantity, int ResourceCapacity, bool RequiresApproval, string Summary);

    private sealed record BookingDto(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);

    private sealed record PagedResultDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
}
