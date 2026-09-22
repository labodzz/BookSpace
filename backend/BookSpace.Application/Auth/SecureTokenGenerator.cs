using System.Security.Cryptography;
using System.Text;

namespace BookSpace.Application.Auth;

// Shared by every single-use, hash-stored secret token this codebase issues (RefreshToken, Invitation)
// - a plain fast hash (SHA-256, not PBKDF2) is correct here because these are high-entropy random
// values, not user-chosen passwords: the hash only needs to stop a raw DB leak from being directly
// usable, and needs to be cheap enough to run on every request that presents one. Extracted from
// AuthenticationService (which originally had this as two private methods) once InviteUserCommandHandler
// needed the exact same generate-and-hash logic for invitation tokens - duplicating a security-critical
// algorithm across two independent copies is exactly the kind of thing that quietly drifts.
internal static class SecureTokenGenerator
{
    public static string GenerateRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string HashToken(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
