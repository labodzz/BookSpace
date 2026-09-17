using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Auth;

public sealed record LoginCommandRequest(string Email, string Password) : IRequest<LoginResponse>;

// ErrorCode is set only on failure (Succeeded == false) - AuthController reads it to attach the same
// machine-readable errorCode extension every other error response carries, without needing to
// reference the (internal, Application-only) ErrorCodes class itself.
public sealed record LoginResponse(bool Succeeded, AuthTokens? Tokens, string? ErrorCode = null);

public sealed class LoginCommandRequestValidator : AbstractValidator<LoginCommandRequest>
{
    public LoginCommandRequestValidator()
    {
        RuleFor(command => command.Email).NotEmpty().EmailAddress();
        RuleFor(command => command.Password).NotEmpty();
    }
}

public sealed class LoginCommandRequestHandler(IAuthenticationService authenticationService)
    : IRequestHandler<LoginCommandRequest, LoginResponse>
{
    public Task<LoginResponse> Handle(LoginCommandRequest request, CancellationToken cancellationToken) =>
        authenticationService.LoginAsync(request.Email, request.Password, cancellationToken);
}
