using BookSpace.Domain.Entities;

namespace BookSpace.Application.Security;

public interface IJwtTokenGenerator
{
    AccessToken GenerateAccessToken(User user, IReadOnlyCollection<string> roles);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);
