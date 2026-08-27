using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Auth;

public sealed record LoginCommand(string Email, string Password) : IRequest<LoginResult>;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(command => command.Email).NotEmpty().EmailAddress();
        RuleFor(command => command.Password).NotEmpty();
    }
}

public sealed class LoginCommandHandler(IAuthenticationService authenticationService) : IRequestHandler<LoginCommand, LoginResult>
{
    public Task<LoginResult> Handle(LoginCommand request, CancellationToken cancellationToken) =>
        authenticationService.LoginAsync(request.Email, request.Password, cancellationToken);
}
