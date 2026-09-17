using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Auth;

public sealed record LogoutCommandRequest(string RefreshToken) : IRequest<LogoutResponse>;

// Empty by design: logout never reports whether the presented token was found or already invalid -
// same "don't leak state" reasoning as login/refresh's indistinguishable failure messages - and the
// client clears its own local state unconditionally regardless of what the server did with the token.
public sealed record LogoutResponse;

public sealed class LogoutCommandRequestValidator : AbstractValidator<LogoutCommandRequest>
{
    public LogoutCommandRequestValidator()
    {
        RuleFor(command => command.RefreshToken).NotEmpty();
    }
}

public sealed class LogoutCommandRequestHandler(IAuthenticationService authenticationService)
    : IRequestHandler<LogoutCommandRequest, LogoutResponse>
{
    public Task<LogoutResponse> Handle(LogoutCommandRequest request, CancellationToken cancellationToken) =>
        authenticationService.LogoutAsync(request.RefreshToken, cancellationToken);
}
