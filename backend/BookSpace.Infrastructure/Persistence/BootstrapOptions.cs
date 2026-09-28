namespace BookSpace.Infrastructure.Persistence;

// Bound from configuration ("Bootstrap" section). Governs the one-time creation of the first tenant
// and first TenantAdmin in an otherwise-empty production database - see ProductionBootstrapper and
// docs/container-deployment.md. Disabled by default: an unconfigured deployment must never attempt
// this, and every field below is required only in the sense that ProductionBootstrapper validates
// them itself (throwing, which fails startup loudly) when Enabled is true - they are all optional
// here so binding a completely absent "Bootstrap" section never throws on its own.
public sealed class BootstrapOptions
{
    public bool Enabled { get; init; }
    public string? TenantName { get; init; }
    public string? DefaultTimeZoneId { get; init; }
    public string? AdminFirstName { get; init; }
    public string? AdminLastName { get; init; }
    public string? AdminEmail { get; init; }
    public string? AdminPassword { get; init; }
}
