using System.Reflection;
using BookSpace.Application.AiBooking;
using BookSpace.Application.Auth;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Calendar;
using BookSpace.Application.Common;
using BookSpace.Application.Email;
using BookSpace.Application.Logging;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookSpace.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        // Checked once per authenticated API request (see BookSpace.Api's middleware) - see
        // IDeletedTenantAccessGuard's own doc comment for why this exists alongside AuthenticationService's
        // own IsUsableAsync rather than reusing it.
        services.AddScoped<IDeletedTenantAccessGuard, DeletedTenantAccessGuard>();
        services.AddSingleton<ICorrelationIdContext, CorrelationIdContext>();

        // Scoped: resolved fresh per cycle by BackgroundJobsWorker (BookSpace.Api), never held by that
        // singleton itself - see docs/background-jobs.md ("Notification outbox cycle").
        services.AddScoped<IBackgroundJobCycle, NotificationOutboxJobCycle>();
        services.AddSingleton<IValidateOptions<BackgroundJobsOptions>, BackgroundJobsOptionsValidator>();

        // Scoped to match IBookingReminderReader/INotificationOutboxWriter, which both need the scoped
        // BookSpaceDbContext - see docs/background-jobs.md ("Reminder job").
        services.AddScoped<IReminderProducer, ReminderProducer>();

        // Stateless - safe as a singleton. See docs/background-jobs.md ("Email templates").
        services.AddSingleton<IEmailTemplateRenderer, BookingEmailTemplateRenderer>();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        // Calendar subscription feed (WP-8) - see docs/calendar-subscription-feed.md.
        services.AddSingleton<IValidateOptions<CalendarFeedOptions>, CalendarFeedOptionsValidator>();

        // App-wide public base URL (tenant onboarding's invitation link) - see docs/tenant-onboarding.md.
        services.AddSingleton<IValidateOptions<AppOptions>, AppOptionsValidator>();

        // AI-assisted booking (WP-9) - see docs/ai-assisted-booking.md.
        services.AddSingleton<IValidateOptions<AiBookingOptions>, AiBookingOptionsValidator>();

        // Singleton: one stable OwnerId, and one lease coordinator holding only IServiceScopeFactory, for
        // this instance's entire lifetime - see docs/background-jobs.md ("Job lease lock"). Neither ever
        // holds a scoped IJobLeaseStore/DbContext directly.
        services.AddSingleton<IBackgroundJobInstanceIdentity, BackgroundJobInstanceIdentity>();
        services.AddSingleton<IJobLeaseCoordinator, JobLeaseCoordinator>();

        var applicationAssembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<IMediator, DefaultMediator>();
        services.AddRequestHandlers(applicationAssembly);

        // Registration order is execution order: logging wraps validation wraps the handler, so a
        // validation short-circuit is still logged.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddValidatorsFromAssembly(applicationAssembly);

        return services;
    }

    private static void AddRequestHandlers(this IServiceCollection services, Assembly assembly)
    {
        var registrations = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(implementedInterface => implementedInterface.IsGenericType
                    && implementedInterface.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(implementedInterface => (Service: implementedInterface, Implementation: type)));

        foreach (var (service, implementation) in registrations)
        {
            services.AddScoped(service, implementation);
        }
    }
}
