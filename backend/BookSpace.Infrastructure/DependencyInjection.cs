using BookSpace.Application.AiBooking;
using BookSpace.Application.Auth;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Calendar;
using BookSpace.Application.Notifications;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Infrastructure.AiBooking;
using BookSpace.Infrastructure.Email;
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
        services.AddScoped<BookSpace.Application.Tenants.ITenantRepository, TenantRepository>();
        services.AddScoped<ICalendarSubscriptionRepository, CalendarSubscriptionRepository>();
        services.AddScoped<IJobLeaseStore, JobLeaseStore>();
        services.AddScoped<INotificationOutboxWriter, NotificationOutboxWriter>();
        services.AddScoped<INotificationOutboxReader, NotificationOutboxReader>();

        // Reminder job - see docs/background-jobs.md ("Reminder job"). Both Scoped: both need the scoped
        // BookSpaceDbContext, and IBookingReminderEligibilityChecker is resolved from the same per-item DI
        // scope NotificationOutboxProcessor already creates, matching IEmailRecipientResolver's lifetime.
        services.AddScoped<IBookingReminderReader, BookingReminderReader>();
        services.AddScoped<IBookingReminderEligibilityChecker, BookingReminderEligibilityChecker>();
        services.AddScoped<IBookingReminderMarker, BookingReminderMarker>();

        // Stale-approval job - see docs/background-jobs.md ("Stale-approval job"). IStaleApprovalReader is
        // Scoped (needs the scoped BookSpaceDbContext, one call per cycle); IStaleApprovalProcessor is
        // Scoped but - like NotificationOutboxProcessor - only ever holds IServiceScopeFactory itself, and
        // creates its own fresh per-candidate scope internally.
        services.AddScoped<IStaleApprovalReader, StaleApprovalReader>();
        services.AddScoped<IStaleApprovalProcessor, StaleApprovalProcessor>();
        // No-show job - see docs/background-jobs.md ("No-show job"). Same Scoped shape as the stale-approval
        // registrations above, for the identical reasons.
        services.AddScoped<INoShowReader, NoShowReader>();
        services.AddScoped<INoShowProcessor, NoShowProcessor>();
        // Scoped, not singleton: it only ever holds IServiceScopeFactory itself (safe at any lifetime),
        // but letting it be Scoped avoids ever forcing a future INotificationSender implementation that
        // needs its own scoped dependencies (a scoped HttpClient, say) into a captive-dependency problem.
        services.AddScoped<INotificationOutboxProcessor, NotificationOutboxProcessor>();

        // Gmail SMTP sender - see docs/background-jobs.md ("Gmail SMTP sender"). IEmailRecipientResolver
        // is Scoped (needs the scoped BookSpaceDbContext); INotificationSender is Scoped to match it -
        // NotificationOutboxProcessor already resolves a fresh one per individual outbox item via its own
        // per-item DI scope, so this never becomes a captive-dependency problem. The transport factory is
        // a stateless Singleton: it only ever hands out brand new, never-shared ISmtpTransport instances
        // (see MailKitSmtpTransportFactory) - the transport itself is never registered in DI at all.
        services.AddScoped<IEmailRecipientResolver, EmailRecipientResolver>();
        services.AddSingleton<ISmtpTransportFactory, MailKitSmtpTransportFactory>();
        services.AddScoped<INotificationSender, SmtpNotificationSender>();

        // AI-assisted booking (WP-9) - see docs/ai-assisted-booking.md. Registered unconditionally,
        // exactly like INotificationSender above: AiBookingOptions.Enabled gates whether
        // ProposeBookingCommandHandler ever actually calls this, not whether it exists in the container -
        // registering a typed HttpClient costs nothing at startup and makes no network call until invoked,
        // so AiBooking:Enabled=false (the default) never requires an API key or any other configuration
        // here. Scoped to match the per-request lifetime every other scoped dependency in this chain uses;
        // AddHttpClient's own handler pooling means this is never the "a new HttpClient per request" anti-
        // pattern despite the Scoped registration.
        services.AddHttpClient<IBookingIntentInterpreter, GeminiBookingIntentInterpreter>((serviceProvider, client) =>
        {
            var aiBookingOptions = serviceProvider.GetRequiredService<IOptions<AiBookingOptions>>().Value;
            client.BaseAddress = new Uri(aiBookingOptions.BaseUrl);
            // A few seconds of slack over ProposeBookingCommandHandler's own CancellationTokenSource
            // timeout, which is the real, authoritative bound (see AiBookingOptions.RequestTimeoutSeconds)
            // - this is only a backstop against HttpClient itself hanging past that for some other reason.
            client.Timeout = TimeSpan.FromSeconds(aiBookingOptions.RequestTimeoutSeconds + 5);
        });

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
