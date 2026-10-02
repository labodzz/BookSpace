using System.Net;
using System.Net.Http.Json;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookSpace.Api.Tests.Routing;

// Proves the actual contract the /api prefix exists to guarantee, with a real (if minimal) wwwroot/
// index.html behind the SPA fallback - unlike HealthEndpointsTests's deep-link check, which only proves
// the fallback route is reached unauthenticated (its factory has no built wwwroot at all, so the file
// itself 404s). Here the fallback genuinely serves a file, so a test can tell "served index.html" apart
// from "404'd" by content, not just by absence of a 401/403.
public sealed class ApiRoutePrefixAndSpaFallbackTests : IClassFixture<SpaWebApplicationFactory>
{
    private readonly SpaWebApplicationFactory _factory;

    public ApiRoutePrefixAndSpaFallbackTests(SpaWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/bookings")]
    [InlineData("/resources")]
    [InlineData("/calendar")]
    [InlineData("/resources/00000000-0000-0000-0000-000000000000/availability")]
    public async Task GetAnAngularClientRoute_WithNoApiPrefix_ServesTheSpaIndexPageUnauthenticated(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(SpaWebApplicationFactory.IndexHtmlMarker, body);
    }

    // The regression this guards against: MapFallbackToFile("index.html") with no route-pattern
    // exclusion matches ANY unmatched request, including one under /api - so a typo'd or unsupported API
    // call would silently come back as a 200 with the SPA shell instead of the 404 a caller needs to
    // actually detect the mistake.
    [Fact]
    public async Task GetAnUnmatchedApiRoute_ReturnsNotFound_AndNeverServesTheSpaIndexPage()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SpaWebApplicationFactory.IndexHtmlMarker, body);
    }

    [Fact]
    public async Task GetTheBareApiPrefixWithNothingAfterIt_ReturnsNotFound_AndNeverServesTheSpaIndexPage()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SpaWebApplicationFactory.IndexHtmlMarker, body);
    }

    // An anonymous 401 here can only come from ResourcesController's own [Authorize] gate - proving
    // routing actually reached the controller, not the SPA fallback (which would 200 with index.html)
    // or a bare unmatched-route 404.
    [Fact]
    public async Task GetARealApiRoute_StillReachesItsController_UnaffectedByTheSpaFallback()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/resources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostLogin_AtTheNewApiAuthPrefix_IsRoutedToTheAuthControllerNotTheSpaFallback()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@bookspace.test", password = "wrong" });

        // AuthController.Login never throws for bad credentials - it returns 401 as an ordinary result
        // (see AuthController's own comment on UnauthorizedProblemAsync), so this - not 200/404 - is what
        // proves the request reached the controller.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

// A real, minimal wwwroot/index.html behind the fallback (unlike CustomWebApplicationFactory, which has
// none) - specifically so a test can assert "the SPA shell was actually served" by content, not merely
// infer it from the absence of a 401/403 the way HealthEndpointsTests's deep-link check has to.
public sealed class SpaWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string IndexHtmlMarker = "bookspace-spa-fallback-marker";

    private readonly string _webRootPath = Path.Combine(Path.GetTempPath(), "bookspace-test-wwwroot-" + Guid.NewGuid());
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SpaWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__BookSpace",
            "Server=unused;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        Environment.SetEnvironmentVariable("Auth__Issuer", "bookspace-tests");
        Environment.SetEnvironmentVariable("Auth__Audience", "bookspace-tests");
        Environment.SetEnvironmentVariable("Auth__SigningKey", "integration-test-signing-key-needs-to-be-long-enough-1234567890");
        Environment.SetEnvironmentVariable("Auth__AccessTokenMinutes", "15");
        Environment.SetEnvironmentVariable("Auth__RefreshTokenDays", "14");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "http://localhost:4200");

        Directory.CreateDirectory(_webRootPath);
        File.WriteAllText(Path.Combine(_webRootPath, "index.html"), $"<!doctype html><html><body>{IndexHtmlMarker}</body></html>");

        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseWebRoot(_webRootPath);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BookSpaceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BookSpaceDbContext>>();
            services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlite(_connection));
        });
    }

    // The login test needs a real, queryable (if empty) schema - without this, any EF query against the
    // freshly-opened SQLite in-memory connection throws "no such table", surfacing as an unrelated 500
    // instead of the 401 a bad-credentials login is actually supposed to produce.
    async Task IAsyncLifetime.InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            try
            {
                Directory.Delete(_webRootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only - a leftover temp directory under %TEMP% is harmless.
            }
        }
    }
}
