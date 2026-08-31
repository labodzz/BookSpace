using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateBlackoutPeriodCommand(Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)
    : IRequest<BlackoutPeriodResponse>;

public sealed record BlackoutPeriodResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed class CreateBlackoutPeriodCommandValidator : AbstractValidator<CreateBlackoutPeriodCommand>
{
    public CreateBlackoutPeriodCommandValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.EndUtc).GreaterThan(command => command.StartUtc);
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(1000);
    }
}

// No overlap rejection - same reasoning as AvailabilityRule: overlapping blackouts on the same
// resource are harmless, since the availability query merges them before subtracting busy time.
public sealed class CreateBlackoutPeriodCommandHandler(
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IResourceRepository resourceRepository,
    ICurrentUserContext currentUserContext) : IRequestHandler<CreateBlackoutPeriodCommand, BlackoutPeriodResponse>
{
    public async Task<BlackoutPeriodResponse> Handle(CreateBlackoutPeriodCommand request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);
        }

        var period = new BlackoutPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceId = request.ResourceId,
            StartUtc = request.StartUtc,
            EndUtc = request.EndUtc,
            Reason = request.Reason,
        };

        await blackoutPeriodRepository.AddAsync(period, cancellationToken);
        await blackoutPeriodRepository.SaveChangesAsync(cancellationToken);

        return period.ToResponse();
    }
}

internal static class BlackoutPeriodMappingExtensions
{
    public static BlackoutPeriodResponse ToResponse(this BlackoutPeriod period) =>
        new(period.Id, period.ResourceId, period.StartUtc, period.EndUtc, period.Reason);
}
