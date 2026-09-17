using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Auth;

public sealed record RefreshCommandRequest(string RefreshToken) : IRequest<RefreshResponse>;

// ErrorCode is set only on failure (Succeeded == false) - AuthController reads it to attach the same
// machine-readable errorCode extension every other error response carries, without needing to
// reference the (internal, Application-only) ErrorCodes class itself.
public sealed record RefreshResponse(bool Succeeded, AuthTokens? Tokens, bool ReuseDetected, string? ErrorCode = null);

public sealed class RefreshCommandRequestValidator : AbstractValidator<RefreshCommandRequest>
{
    public RefreshCommandRequestValidator()
    {
        RuleFor(command => command.RefreshToken).NotEmpty();
    }
}

public sealed class RefreshCommandRequestHandler(IAuthenticationService authenticationService)
    : IRequestHandler<RefreshCommandRequest, RefreshResponse>
{
    public Task<RefreshResponse> Handle(RefreshCommandRequest request, CancellationToken cancellationToken) =>
        authenticationService.RefreshAsync(request.RefreshToken, cancellationToken);
}
