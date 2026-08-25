namespace BookSpace.Domain.Entities;

// FamilyId links every token descended from one login. Rotation keeps FamilyId unchanged and points
// the old token at its successor via ReplacedByTokenId; presenting a token that already has one is
// exactly the signal that a stolen token is being replayed, and revokes every token in the family.
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
