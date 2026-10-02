using BookSpace.Application.Auth;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Notifications;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("BookSpace")
            ?? throw new InvalidOperationException("Connection string 'BookSpace' is not configured.");

        // A serverless Azure SQL database can be paused and take a moment to resume, so the very first
        // connection after idle can transiently fail - retried automatically here rather than surfacing
        // as a hard error. Bounded (not infinite): a genuinely down/misconfigured database still fails
        // after the last retry, it just no longer fails on the *first* transient blip.
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(
            connectionString,
            sqlOptions => sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.Configure<BootstrapOptions>(configuration.GetSection("Bootstrap"));
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IInvitationRepository, InvitationRepository>();
        services.AddScoped<IResourceRepository, ResourceRepository>();
        services.AddScoped<IResourceTypeRepository, ResourceTypeRepository>();
        services.AddScoped<IAvailabilityRuleRepository, AvailabilityRuleRepository>();
        services.AddScoped<IBlackoutPeriodRepository, BlackoutPeriodRepository>();
        services.AddScoped<IResourceApproverRepository, ResourceApproverRepository>();
        services.AddScoped<IBookingAvailabilityRepository, BookingAvailabilityRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IResourceBookingLock, ResourceBookingLock>();
        services.AddScoped<IRecurringSeriesRepository, RecurringSeriesRepository>();
        services.AddScoped<IApprovalRequestRepository, ApprovalRequestRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IJobLeaseStore, JobLeaseStore>();
        services.AddScoped<INotificationOutboxWriter, NotificationOutboxWriter>();
        services.AddScoped<INotificationOutboxReader, NotificationOutboxReader>();
        // Scoped, not singleton: it only ever holds IServiceScopeFactory itself (safe at any lifetime),
        // but letting it be Scoped avoids ever forcing a future INotificationSender implementation that
        // needs its own scoped dependencies (a scoped HttpClient, say) into a captive-dependency problem.
        // No INotificationSender is registered here - see docs/background-jobs.md ("Why this is not wired
        // into the worker yet"): there is no real sender yet, so resolving this processor's dependencies
        // for real use would correctly fail fast rather than silently pretending to send email.
        services.AddScoped<INotificationOutboxProcessor, NotificationOutboxProcessor>();

        return services;
    }

    // Dev-only convenience: applies pending migrations and seeds a realistic dataset on startup.
    // Called from Program.cs only when the environment is Development.
    public static async Task MigrateAndSeedDevelopmentDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.MigrateAsync(cancellationToken);
        await DevelopmentSeeder.SeedAsync(dbContext, passwordHasher, cancellationToken);
    }

    // Production counterpart to the block above: applies pending migrations without ever seeding
    // (DevelopmentSeeder's known test accounts/password must never reach a real database). Only called
    // from Program.cs when Database:ApplyMigrationsOnStartup is explicitly true - see DatabaseOptions
    // and docs/container-deployment.md for the maxReplicas=1 requirement this implies.
    public static async Task MigrateProductionDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    // Only called from Program.cs after any migration step above has already completed - see
    // ProductionBootstrapper for the idempotency/validation rules and BootstrapOptions for the
    // configuration shape.
    public static async Task BootstrapProductionAdminAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BookSpace.Bootstrap");

        await ProductionBootstrapper.SeedAsync(dbContext, passwordHasher, options, logger, cancellationToken);
    }
}
