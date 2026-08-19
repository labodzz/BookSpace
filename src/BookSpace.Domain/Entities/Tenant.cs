namespace BookSpace.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
