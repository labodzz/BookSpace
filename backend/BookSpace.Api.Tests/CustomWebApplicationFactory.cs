using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookSpace.Api.Tests;

// Swaps the real SQL Server DbContext for a SQLite in-memory one and forces a non-Development
// environment so Program.cs's Development-only migrate/seed step (written against SQL Server
// migrations) never runs against it. The connection is kept open for the factory's lifetime -
// SQLite's ":memory:" database is destroyed the moment its one connection closes.
//
// The environment/connection-string/auth values are set as real process environment variables,
// not via ConfigureWebHost's ConfigureAppConfiguration: Program.cs's own top-level
// WebApplicationBuilder.CreateBuilder(args) call reads configuration and throws on a missing
// connection string *before* WebApplicationFactory's ConfigureWebHost hooks ever get applied, so
// only values visible to the process at that point (real env vars) can prevent that throw.
//
// Implements xUnit's IAsyncLifetime (not just seeding per-test) because IClassFixture gives every
// test method its own test-class instance but the SAME factory/database - seeding needs to happen
// exactly once, before the first test runs, not once per test method.
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public CustomWebApplicationFactory()
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

        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BookSpaceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BookSpaceDbContext>>();
            services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlite(_connection));
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.EnsureCreatedAsync();
        await TestDataSeeder.SeedAsync(dbContext, passwordHasher);
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
        }
    }
}
