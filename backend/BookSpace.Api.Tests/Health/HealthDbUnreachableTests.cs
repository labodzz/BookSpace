using System.Net;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookSpace.Api.Tests.Health;

public sealed class HealthDbUnreachableTests : IClassFixture<UnreachableDatabaseWebApplicationFactory>
{
    private readonly UnreachableDatabaseWebApplicationFactory _factory;

    public HealthDbUnreachableTests(UnreachableDatabaseWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetHealthDb_WhenTheDatabaseIsUnreachable_ReturnsServiceUnavailable()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/db");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

// A second, standalone factory (not CustomWebApplicationFactory) because this needs a DbContext that
// can never connect - CustomWebApplicationFactory's SQLite in-memory database is always reachable by
// construction, so it can't exercise this branch. Deliberately does NOT call EnableRetryOnFailure the
// way BookSpace.Infrastructure.DependencyInjection.AddInfrastructure does for the real app - retrying
// against an address that will never answer would make this test slow and this is specifically testing
// CanConnectAsync's own true/false result, not the retry policy.
public sealed class UnreachableDatabaseWebApplicationFactory : WebApplicationFactory<Program>
{
    public UnreachableDatabaseWebApplicationFactory()
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
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BookSpaceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BookSpaceDbContext>>();
            // 169.254.x.x is link-local and never routable off this host, so the connection attempt
            // fails fast instead of waiting out a real network timeout; Connect Timeout=1 bounds it
            // further just in case.
            services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(
                "Server=169.254.1.1,1;Database=unreachable;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=1"));
        });
    }
}
