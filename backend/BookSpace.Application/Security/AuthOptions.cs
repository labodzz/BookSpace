namespace BookSpace.Application.Security;

// Bound from configuration ("Auth" section). SigningKey is deliberately absent from the tracked
// appsettings files (including Development) - it lives in local user secrets in dev and belongs in a
// real secret manager in production. Startup fails fast (see Program.cs) if it's missing.
public sealed class AuthOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 14;
}
