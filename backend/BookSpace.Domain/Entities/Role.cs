namespace BookSpace.Domain.Entities;

public sealed class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class UserRole
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Guid UserId { get; set; }
}
