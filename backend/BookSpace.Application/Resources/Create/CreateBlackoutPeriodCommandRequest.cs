using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateBlackoutPeriodCommandRequest(Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)
    : IRequest<CreateBlackoutPeriodResponse>;

public sealed record CreateBlackoutPeriodResponse(Guid Id, Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed class CreateBlackoutPeriodCommandRequestValidator : AbstractValidator<CreateBlackoutPeriodCommandRequest>
{
    public CreateBlackoutPeriodCommandRequestValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.StartUtc)
            .Must(startUtc => startUtc >= DateTimeOffset.UtcNow)
            .WithMessage("Blackout period cannot start in the past.");
        RuleFor(command => command.EndUtc).GreaterThan(command => command.StartUtc);
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(1000);
    }
}

// No overlap rejection - same reasoning as AvailabilityRule: overlapping blackouts on the same
// resource are harmless, since the availability query merges them before subtracting busy time.
public sealed class CreateBlackoutPeriodCommandHandler(
    IBlackoutPeriodRepository blackoutPeriodRepository,
    IResourceRepository resourceRepository,
    ICurrentUserContext currentUserContext) : IRequestHandler<CreateBlackoutPeriodCommandRequest, CreateBlackoutPeriodResponse>
{
    public async Task<CreateBlackoutPeriodResponse> Handle(CreateBlackoutPeriodCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        // Archived is terminal - see the identical check/reasoning in CreateAvailabilityRuleCommandRequest.cs.
        if (resource.Status == ResourceStatus.Archived)
        {
            throw new ConflictException($"Resource {request.ResourceId} is archived and cannot be modified.");
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

        return new CreateBlackoutPeriodResponse(period.Id, period.ResourceId, period.StartUtc, period.EndUtc, period.Reason);
    }
}
