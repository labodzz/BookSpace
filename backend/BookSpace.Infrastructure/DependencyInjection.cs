using BookSpace.Infrastructure.Persistence;
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

        return services;
    }

    // Dev-only convenience: applies pending migrations and seeds a realistic dataset on startup.
    // Called from Program.cs only when the environment is Development.
    public static async Task MigrateAndSeedDevelopmentDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        await dbContext.Database.MigrateAsync(cancellationToken);
        await DevelopmentSeeder.SeedAsync(dbContext, cancellationToken);
    }
}
