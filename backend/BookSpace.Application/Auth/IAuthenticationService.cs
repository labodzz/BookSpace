namespace BookSpace.Application.Auth;

public interface IAuthenticationService
{
    Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<RefreshResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task<LogoutResponse> LogoutAsync(string refreshToken, CancellationToken cancellationToken);
    Task<AcceptInvitationResponse> AcceptInvitationAsync(string token, string password, CancellationToken cancellationToken);
}

// Same plain-result-record pattern as LoginResponse/RefreshResponse (see LoginCommandRequest.cs/
// RefreshCommandRequest.cs) - AcceptInvitationAsync never throws for an ordinary "invalid token"
// outcome. Defined here rather than in AcceptInvitationCommandRequest.cs (see that file's own comment)
// since IAuthenticationService is this response's true owner, the same way LoginResponse's home is
// really about what LoginAsync returns, not about LoginCommandRequest specifically. No Tokens field:
// accepting an invitation never issues a session - the caller logs in separately afterward via
// POST /api/auth/login (see docs/user-administration.md §4).
public sealed record AcceptInvitationResponse(bool Succeeded, string? ErrorCode = null);
