using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookSpace.Application.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookSpace.Api.Tests.ErrorHandling;

// End-to-end coverage that a genuinely unexpected exception - not FluentValidation.ValidationException,
// which has its own handler and its own tests - still comes back as a clean, generic ProblemDetails
// response with a correlation ID, never the exception's own message or a stack trace. Swaps in a
// throwing ICurrentUserContext for one request rather than adding a test-only endpoint to production
// code, so the request travels through the real mediator pipeline and the real exception handler chain.
public sealed class GlobalExceptionHandlerEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string SimulatedFailureMessage = "Simulated unexpected failure for GlobalExceptionHandler test coverage.";

    private readonly CustomWebApplicationFactory _factory;

    public GlobalExceptionHandlerEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UnhandledException_ReturnsGeneric500ProblemDetailsWithCorrelationIdAndNoInternalDetails()
    {
        await using var throwingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentUserContext>();
                services.AddScoped<ICurrentUserContext, ThrowingCurrentUserContext>();
            });
        });
        using var client = throwingFactory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            email = TestDataSeeder.AcmeAdminEmail,
            password = TestDataSeeder.Password,
        });
        var loginBodyText = await loginResponse.Content.ReadAsStringAsync();
        Assert.True(loginResponse.IsSuccessStatusCode, $"Login failed: {(int)loginResponse.StatusCode} {loginBodyText}");
        var loginBody = JsonSerializer.Deserialize<LoginResponse>(loginBodyText, JsonOptions);
        client.DefaultRequestHeaders.Authorization = new("Bearer", loginBody!.AccessToken);

        var response = await client.GetAsync("/users/me");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SimulatedFailureMessage, payload);
        Assert.DoesNotContain(nameof(InvalidOperationException), payload);
        Assert.DoesNotContain(nameof(ThrowingCurrentUserContext), payload);

        var problem = JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
        Assert.True(problem.TryGetProperty("correlationId", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));
    }

    // TenantId must stay harmless: BookSpaceDbContext's global tenant query filter reads it on every
    // query against a tenant-owned table, including the one login itself runs to look up the user by
    // email - throwing there would break login, not just the /users/me call this test targets.
    private sealed class ThrowingCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => throw new InvalidOperationException(SimulatedFailureMessage);

        public Guid? TenantId => null;

        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed record LoginResponse(string AccessToken);
}
