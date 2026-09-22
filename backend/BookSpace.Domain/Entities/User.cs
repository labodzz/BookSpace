using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

public sealed class User : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
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
