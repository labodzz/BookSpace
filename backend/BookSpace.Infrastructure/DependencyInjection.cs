using BookSpace.Application.Auth;
using BookSpace.Application.Bookings;
using BookSpace.Application.ResourceTypes;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("BookSpace")
            ?? throw new InvalidOperationException("Connection string 'BookSpace' is not configured.");

        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(connectionString));

        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
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
}
