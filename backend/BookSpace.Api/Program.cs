using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.RateLimiting;
using BookSpace.Api.BackgroundJobs;
using BookSpace.Api.ErrorHandling;
using BookSpace.Api.Logging;
using BookSpace.Api.Security;
using BookSpace.Application;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Logging;
using BookSpace.Application.Security;
using BookSpace.Infrastructure;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// UseSerilogRequestLogging() below writes its per-request summary line through this static logger,
// not through the fully configured one from builder.Host.UseSerilog - so both need the same console
// template, or the correlation ID vanishes from exactly the line most useful for following a request
// in the console. {CorrelationId} renders blank when no request is in flight (startup, shutdown).
const string ConsoleOutputTemplate =
    "{Timestamp:HH:mm:ss} [{Level:u3}] (cid: {CorrelationId}) {Message:lj}{NewLine}{Exception}";

const string CorsPolicyName = "Frontend";
const string LoginRateLimiterPolicyName = "login";

// Two-stage setup (the pattern Serilog itself recommends): a minimal bootstrap logger captures
// anything that goes wrong before configuration/DI are even up, then builder.Host.UseSerilog below
// replaces it with the fully configured logger for the rest of the app's life.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
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
        // Off by default everywhere, including Production: a container's non-root user has no write
        // access to a local "logs" directory, and Azure Container Apps collects stdout/stderr anyway
        // (the console sink above is never disabled). Explicitly set Logging__WriteStructuredFile=true
        // for a non-container deployment that wants the queryable compact-JSON file store back.
        var writeStructuredFile = context.Configuration.GetValue<bool?>("Logging:WriteStructuredFile") ?? false;

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "BookSpace.Api")
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate);

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

    // Fail closed, same principle as the tenant query filter: an unconfigured/empty origin list means
    // WithOrigins() is never called, so the policy allows no cross-origin requests at all, rather than
    // falling back to an open AllowAnyOrigin(). The frontend calls this API with an Authorization: Bearer
    // header, never cookies, so AllowCredentials() is deliberately not set - it isn't needed, and setting
    // it would also forbid combining this policy with a wildcard origin later by mistake.
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(CorsPolicyName, policy =>
        {
            policy.WithOrigins(corsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
        });
    });

    // Per-client-IP, not global - a shared bucket across every caller would let one busy IP lock
    // everyone else out of login. 10 attempts/minute is generous for a mistyped password or a
    // legitimate retry, while still bounding a brute-force/credential-stuffing script. Deliberately
    // login-only, not a blanket API-wide limit - the rest of the API is already behind auth, where a
    // valid bearer token is itself a much stronger gate than a request-rate ceiling.
    //
    // Effectively disabled (a limit no real test run can reach) in Testing: WebApplicationFactory's
    // in-memory TestServer gives every request the same RemoteIpAddress (or none), so the entire
    // integration suite's login traffic - hundreds of logins across unrelated tests - would otherwise
    // collapse into one partition and trip the real limit, failing tests on a TestServer artifact
    // rather than a genuine brute-force signal.
    var isTestingEnvironment = builder.Environment.EnvironmentName == "Testing";
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy(LoginRateLimiterPolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isTestingEnvironment ? int.MaxValue : 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Matches the ProblemDetails+correlationId shape every other error response uses (see
        // NotFoundExceptionHandler etc.) - a 429 from /auth/login shouldn't look different from any
        // other error the API returns.
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = "Too many login attempts. Please wait a moment and try again.",
            };

            var correlationIdContext = context.HttpContext.RequestServices.GetRequiredService<ICorrelationIdContext>();
            if (correlationIdContext.CorrelationId is { } correlationId)
            {
                problemDetails.Extensions["correlationId"] = correlationId;
                context.HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
            }

            var problemDetailsService = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails = problemDetails,
            });
        };
    });

    builder.Services.AddProblemDetails();
    // Tried in registration order, first to return true wins: specific exception types are mapped
    // first (validation -> 400, not found -> 404, conflict -> 409); GlobalExceptionHandler is the
    // catch-all fallback for everything else, so it's registered last.
    builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
    builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
    builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // WP-8 background jobs foundation only - see docs/background-jobs.md. ValidateOnStart() runs
    // BackgroundJobsOptionsValidator immediately at startup (not lazily on first resolution), so a
    // misconfigured PollIntervalSeconds fails fast instead of only surfacing once the worker polls.
    // Disabled by default (BackgroundJobsOptions.Enabled), so this has no effect on Development,
    // Testing, or an unconfigured deployment until BackgroundJobs:Enabled=true is set explicitly.
    builder.Services.AddOptions<BackgroundJobsOptions>()
        .Bind(builder.Configuration.GetSection("BackgroundJobs"))
        .ValidateOnStart();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHostedService<BackgroundJobsWorker>();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();

    // Bound through the same AuthOptions type Infrastructure/DependencyInjection.cs binds for
    // IOptions<AuthOptions> elsewhere (JwtTokenGenerator etc.) - one shape for the "Auth" config
    // section, not two independent readers that could quietly drift out of sync.
    var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>();
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
                ValidIssuer = authOptions?.Issuer,
                ValidateAudience = true,
                ValidAudience = authOptions?.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions?.SigningKey
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

    // Must be the very first middleware: everything downstream (HTTPS redirection below, in
    // particular) makes its scheme-based decisions from HttpContext.Request.Scheme, which this
    // middleware is what corrects from X-Forwarded-Proto in the first place. Azure Container Apps
    // terminates TLS at its own ingress and forwards plain HTTP to the container - without this, the
    // app would see every request as HTTP and UseHttpsRedirection would redirect a request the client
    // already made over HTTPS, forever. Opt-in via ASPNETCORE_FORWARDEDHEADERS_ENABLED=true, not
    // unconditional - a non-container deployment reachable directly from the internet must not trust
    // client-supplied forwarded headers by default.
    if (builder.Configuration.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED"))
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        // Azure Container Apps' own ingress is the only network path that can reach this container -
        // it is never exposed directly to the internet - so the proxy hop is always trusted, and its
        // address isn't a fixed value this could list here instead of clearing.
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeadersOptions);
    }

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        // Anonymous: same reasoning as the /health and SPA-fallback overrides below - the app has a
        // global authenticated-fallback policy (see AddAuthorization above), and without this override
        // the API docs would 401 before anyone could even see them.
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
        await app.Services.MigrateAndSeedDevelopmentDatabaseAsync();
    }

    // Production counterpart to the Development block above - both opt-in via configuration, and
    // mutually exclusive in what they do (never seeds). See DatabaseOptions/BootstrapOptions and
    // docs/container-deployment.md. Bootstrap always runs after any migration step, and is itself a
    // no-op unless Bootstrap:Enabled is true - see ProductionBootstrapper.
    if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
    {
        await app.Services.MigrateProductionDatabaseAsync();
    }
    await app.Services.BootstrapProductionAdminAsync();

    // Serves the Angular production build the container's Dockerfile copies into wwwroot. Static
    // files first (and default-file-mapped) so a request for a real built asset - or "/" - is answered
    // directly; the fallbacks below (registered after routing) are what turn an unmatched deep link
    // like /resources/{id} into the Angular app instead of a 404.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Explicit on purpose: in the minimal hosting model, omitting this call makes routing match
    // implicitly at the very start of the pipeline - before the static file middleware above even
    // runs. Since every non-API path now matches one of the broad fallback routes below, that would
    // mean every request already has a matched endpoint (HttpContext.GetEndpoint() != null) by the
    // time UseStaticFiles() executes, and StaticFileMiddleware intentionally skips serving whenever an
    // endpoint has already been selected - so a REAL file like main-XXXX.js or favicon.ico would never
    // be served and every asset request would silently fall through to index.html instead (a blank
    // page, since the browser gets HTML where it expected JS). Calling UseRouting() here, after the
    // static file middleware, defers route matching until this exact point, so a request for a real
    // static file is always answered by the middleware above before routing ever sees it.
    app.UseRouting();

    // Correlation ID first (outermost) so every log below - including the request-logging summary
    // line and anything the exception handler logs - carries it. Request logging wraps the
    // exception handler so its summary line reflects the final status code after an exception has
    // already been turned into a response, not the raw unhandled state.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();

    app.UseHttpsRedirection();

    // Must run before UseAuthentication/UseAuthorization: a preflight OPTIONS request carries no
    // Authorization header at all, so if auth ran first a browser's preflight would be rejected before
    // CORS ever got a chance to answer it.
    app.UseCors(CorsPolicyName);

    app.UseAuthentication();
    app.UseAuthorization();

    // Endpoint-metadata-based ([EnableRateLimiting] on AuthController.Login), so this must run after
    // routing has matched an endpoint - placed alongside UseAuthorization, before the endpoints
    // themselves execute via MapControllers.
    app.UseRateLimiter();

    app.MapControllers();

    // Liveness: never touches the database - Azure Container Apps (or any orchestrator) should be able
    // to tell the process itself is up even while the database is unreachable, so this and /health/db
    // are deliberately separate checks rather than one endpoint conflating both concerns.
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

    // Readiness: a real, awaited round trip to the database. Returns a non-2xx status when unreachable
    // (rather than always 200 with a text field an orchestrator would have to parse) so this composes
    // correctly with standard HTTP health-check tooling. Never echoes the database name - a public,
    // anonymous endpoint has no reason to disclose that.
    app.MapGet("/health/db", async (BookSpaceDbContext dbContext, CancellationToken cancellationToken) =>
    {
        var isConnected = await dbContext.Database.CanConnectAsync(cancellationToken);
        return isConnected
            ? Results.Ok(new { status = "connected" })
            : Results.Json(new { status = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }).AllowAnonymous();

    // Explicit, anonymous 404 for anything under "api/" that MapControllers above didn't claim.
    // AddAuthorization's FallbackPolicy (above) applies RequireAuthenticatedUser() even to a request
    // that matches NO endpoint at all (true since .NET 7) - without this, an unauthenticated call to a
    // nonexistent API route like /api/does-not-exist would come back 401 instead of the plain 404 a
    // caller needs to actually detect the mistake. Registered before the SPA fallback below so a
    // request under "api/" is claimed here first, never reaching that one at all.
    app.MapFallback("api/{**path}", () => Results.NotFound()).AllowAnonymous();

    // Anonymous: the app has a global authenticated-fallback policy (see AddAuthorization above), and
    // without this override an unauthenticated deep link would 401 before Angular's own router ever
    // gets a chance to redirect to /login. Registered last for readability only - MapFallbackToFile
    // endpoints always have the lowest match priority, so a real controller route or an actual static
    // file already served above is never shadowed by this regardless of declaration order.
    //
    // The route pattern excludes anything under "api/" on purpose: every real API endpoint is matched
    // by MapControllers above already, and any other "api/" path is now claimed by the explicit 404
    // fallback just above - an Angular deep link and a typo'd API call should never look the same to
    // the caller.
    app.MapFallbackToFile("{*path:regex(^(?!api(/|$)).*$)}", "index.html").AllowAnonymous();

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
