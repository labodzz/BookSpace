using BookSpace.Application.Mediator;
using FluentValidation;

namespace BookSpace.Application.Auth;

public sealed record RefreshCommand(string RefreshToken) : IRequest<RefreshResult>;

public sealed class RefreshCommandValidator : AbstractValidator<RefreshCommand>
{
    public RefreshCommandValidator()
    {
        RuleFor(command => command.RefreshToken).NotEmpty();
    }
}

public sealed class RefreshCommandHandler(IAuthenticationService authenticationService) : IRequestHandler<RefreshCommand, RefreshResult>
{
    public Task<RefreshResult> Handle(RefreshCommand request, CancellationToken cancellationToken) =>
        authenticationService.RefreshAsync(request.RefreshToken, cancellationToken);
}
