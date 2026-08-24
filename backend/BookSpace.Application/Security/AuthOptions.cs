namespace BookSpace.Application.Security;

// Bound from configuration ("Auth" section). The dev appsettings ships a real signing key so the
// app runs out of the box - that key is dev-only and must never be reused as-is in production, where
// it belongs in a secret manager instead.
public sealed class AuthOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 14;
}
