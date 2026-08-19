using Microsoft.AspNetCore.Identity;

namespace BookSpace.Infrastructure.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
}
