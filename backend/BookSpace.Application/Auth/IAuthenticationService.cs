namespace BookSpace.Application.Auth;

public interface IAuthenticationService
{
    Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<RefreshResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task<LogoutResponse> LogoutAsync(string refreshToken, CancellationToken cancellationToken);
}
