namespace BookSpace.Application.Auth;

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public sealed record LoginResult(bool Succeeded, AuthTokens? Tokens);

public sealed record RefreshResult(bool Succeeded, AuthTokens? Tokens, bool ReuseDetected);
