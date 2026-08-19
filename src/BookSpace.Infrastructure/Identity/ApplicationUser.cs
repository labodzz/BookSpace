using Microsoft.AspNetCore.Identity;

namespace BookSpace.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool IsSuspended { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
