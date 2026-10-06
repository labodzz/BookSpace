namespace BookSpace.Infrastructure.Persistence;

// Bound from configuration ("Bootstrap" section). Governs the one-time creation of the first global
// SysAdmin in an otherwise-empty production database - see ProductionBootstrapper and
// docs/container-deployment.md. Disabled by default: an unconfigured deployment must never attempt
// this, and every field below is required only in the sense that ProductionBootstrapper validates them
// itself (throwing, which fails startup loudly) when Enabled is true - they are all optional here so
// binding a completely absent "Bootstrap" section never throws on its own.
//
// Deliberately does NOT create a tenant: bootstrap's only job is getting the first login-capable account
// into an empty database, and the first account must be the global platform admin (TenantId = null), not
// a tenant + its TenantAdmin - a SysAdmin creates tenants afterward through the ordinary create-tenant
// API/invitation flow (see CreateTenantCommandHandler), the same path every later tenant goes through.
// There is therefore no TenantName/DefaultTimeZoneId here any more - an earlier version of this type had
// them (plus AdminFirstName/AdminLastName/AdminEmail/AdminPassword for a tenant-scoped admin), but since
// this project has never been deployed to production, there is no deployed configuration depending on
// those keys to preserve - a clean rename to Bootstrap:SysAdmin* was safer than carrying dead,
// misleadingly-named "deprecated" fields forward with nothing real using them.
public sealed class BootstrapOptions
{
    public bool Enabled { get; init; }
    public string? SysAdminFirstName { get; init; }
    public string? SysAdminLastName { get; init; }
    public string? SysAdminEmail { get; init; }

    // Only ever used to seed the FIRST platform login - there is no one else yet to send an invitation
    // to. Operator responsibility, documented in docs/container-deployment.md: after the first
    // successful SysAdmin login, remove the Bootstrap:SysAdminPassword secret (and ideally flip
    // Bootstrap:Enabled back to false) so it stops being readable from the deployment's configuration/
    // secret store - this type never logs it, and ProductionBootstrapper never logs it either.
    public string? SysAdminPassword { get; init; }
}
