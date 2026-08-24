using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

public sealed class User : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
