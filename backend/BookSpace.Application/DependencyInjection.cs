using System.Reflection;
using BookSpace.Application.Auth;
using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Logging;
using BookSpace.Application.Mediator;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookSpace.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<ICorrelationIdContext, CorrelationIdContext>();

        // Scoped: resolved fresh per cycle by BackgroundJobsWorker (BookSpace.Api), never held by that
        // singleton itself - see docs/background-jobs.md.
        services.AddScoped<IBackgroundJobCycle, NoOpBackgroundJobCycle>();
        services.AddSingleton<IValidateOptions<BackgroundJobsOptions>, BackgroundJobsOptionsValidator>();

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
