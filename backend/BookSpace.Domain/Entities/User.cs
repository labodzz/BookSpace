using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// Deliberately NOT ITenantOwned: that interface documents a direct, required TenantId - true for every
// other tenant-scoped entity, but not for a User, because exactly one kind of User has no tenant at all
// (the global SysAdmin - see TenantId's own comment). BookSpaceDbContext registers a dedicated, nullable-
// aware query filter for User instead of picking this one up through the generic ITenantOwned loop - see
// BookSpaceDbContext.ApplyTenantIsolationFilters.
public sealed class User
{
    public Guid Id { get; set; }

    // Null only for the global SysAdmin (see TenantStatus - no, see Role "SysAdmin"): a platform
    // administrator who is not a member of any tenant and must never be able to acquire one. Every other
    // user (Member/Approver/TenantAdmin, in any combination) must have this set - enforced at the
    // points that create/accept users (ProductionBootstrapper, DevelopmentSeeder,
    // CreateTenantCommandHandler), not by a NOT NULL column, since the one legitimate null case lives in
    // the same table.
    public Guid? TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    // Defaults to Active so every existing object-initializer call site (DevelopmentSeeder,
    // TestDataSeeder, every test that builds a User by hand) keeps meaning exactly what it always
    // meant - a real, login-capable user - without having to be touched to set this explicitly. Only
    // the new invitation flow ever constructs a User with Status = Invited.
    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
