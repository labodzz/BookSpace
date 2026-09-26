namespace BookSpace.Infrastructure.Persistence;

// Bound from configuration ("Database" section). Startup migrations are opt-in and default to off:
// Development already applies migrations itself (see MigrateAndSeedDevelopmentDatabaseAsync), and an
// unconfigured Production/Testing environment must never silently start mutating schema on boot.
// Container deployment sets Database__ApplyMigrationsOnStartup=true explicitly - see
// docs/container-deployment.md for why maxReplicas must stay at 1 while this is enabled (two instances
// racing Database.MigrateAsync against the same database is not guarded against here).
public sealed class DatabaseOptions
{
    public bool ApplyMigrationsOnStartup { get; init; }
}
