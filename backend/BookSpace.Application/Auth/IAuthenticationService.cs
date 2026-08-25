namespace BookSpace.Application.Auth;

public interface IAuthenticationService
{
    Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}
