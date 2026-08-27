using System.Diagnostics.CodeAnalysis;
using System.Text;
using BookSpace.Api.ErrorHandling;
using BookSpace.Api.Logging;
using BookSpace.Api.Security;
using BookSpace.Application;
using BookSpace.Application.Security;
using BookSpace.Infrastructure;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Two-stage setup (the pattern Serilog itself recommends): a minimal bootstrap logger captures
// anything that goes wrong before configuration/DI are even up, then builder.Host.UseSerilog below
// replaces it with the fully configured logger for the rest of the app's life.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // preserveStaticLogger: true - WebApplicationFactory<Program> (used by the integration tests)
    // builds this host more than once per process. The default false leaves a single static
    // ReloadableLogger that gets "frozen" on the first Build() and throws on the second; true gives
    // every Build() its own independent logger instead. Log.Fatal in the catch block below still
    // goes through the bootstrap logger, which is correct - a startup failure happens before this
    // configured logger even exists.
    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    {
        // Console everywhere (dev convenience, and containers/orchestrators capture stdout anyway).
        // The compact-JSON file sink is the "queryable, not just readable" structured store for
        // everything other than Development - skipped in the Testing environment too, so the test
        // suite never writes log files to disk.
        var writeStructuredFile = !context.HostingEnvironment.IsDevelopment()
            && context.HostingEnvironment.EnvironmentName != "Testing";

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "BookSpace.Api")
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console();

        if (writeStructuredFile)
        {
            loggerConfiguration.WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine("logs", "bookspace-.json"),
                rollingInterval: RollingInterval.Day);
        }
    }, preserveStaticLogger: true);

    // Add services to the container.

    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    builder.Services.AddProblemDetails();
    // Tried in registration order, first to return true wins: ValidationExceptionHandler maps its
    // one specific exception type to a 400 with field errors; GlobalExceptionHandler is the
    // catch-all fallback for everything else, so it's registered last.
    builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();

    var authConfig = builder.Configuration.GetSection("Auth");
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Otherwise short claim names like "sub" get silently remapped to legacy XML-namespace
            // claim URIs, and every claim lookup elsewhere has to know about that translation.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = authConfig["Issuer"],
                ValidateAudience = true,
                ValidAudience = authConfig["Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authConfig["SigningKey"]
                    ?? throw new InvalidOperationException("Auth:SigningKey is not configured."))),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    // Secure by default: any endpoint added later requires an authenticated caller unless it explicitly
    // opts out with [AllowAnonymous] - the same "can't be forgotten" principle as the tenant filter below.
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        await app.Services.MigrateAndSeedDevelopmentDatabaseAsync();
    }

    // Correlation ID first (outermost) so every log below - including the request-logging summary
    // line and anything the exception handler logs - carries it. Request logging wraps the
    // exception handler so its summary line reflects the final status code after an exception has
    // already been turned into a response, not the raw unhandled state.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.MapGet("/health/db", async (BookSpaceDbContext dbContext, CancellationToken cancellationToken) =>
    {
        var isConnected = await dbContext.Database.CanConnectAsync(cancellationToken);
        return Results.Ok(new { database = dbContext.Database.GetDbConnection().Database, status = isConnected ? "connected" : "unreachable" });
    }).AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is how `dotnet ef` tooling short-circuits building the host when it only
    // needs the DbContext for design-time work (migrations) - not a real startup failure.
    Log.Fatal(ex, "BookSpace.Api terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

[ExcludeFromCodeCoverage(Justification = "Composition root - wiring is covered by integration tests running against it, not unit-tested directly.")]
public partial class Program;
