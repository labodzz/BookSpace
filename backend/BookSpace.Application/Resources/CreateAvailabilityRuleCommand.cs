using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateAvailabilityRuleCommand(Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime)
    : IRequest<AvailabilityRuleResponse>;

public sealed record AvailabilityRuleResponse(Guid Id, Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed class CreateAvailabilityRuleCommandValidator : AbstractValidator<CreateAvailabilityRuleCommand>
{
    public CreateAvailabilityRuleCommandValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.DayOfWeek).IsInEnum();
        RuleFor(command => command.EndTime).GreaterThan(command => command.StartTime);
    }
}

// No overlap rejection beyond the exact-duplicate check below - the availability query merges
// overlapping same-day rules before subtracting busy time, so two overlapping-but-different rules
// for the same day are harmless by design, not a data integrity problem.
public sealed class CreateAvailabilityRuleCommandHandler(
    IAvailabilityRuleRepository availabilityRuleRepository,
    IResourceRepository resourceRepository,
    ICurrentUserContext currentUserContext) : IRequestHandler<CreateAvailabilityRuleCommand, AvailabilityRuleResponse>
{
    public async Task<AvailabilityRuleResponse> Handle(CreateAvailabilityRuleCommand request, CancellationToken cancellationToken)
    {
        if (await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken) is null)
        {
            throw new NotFoundException($"Resource {request.ResourceId} was not found.");
        }

        if (await availabilityRuleRepository.ExistsAsync(
                request.ResourceId, request.DayOfWeek, request.StartTime, request.EndTime, cancellationToken))
        {
            throw new ConflictException("An identical availability rule already exists for this resource.");
        }

        var rule = new AvailabilityRule
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceId = request.ResourceId,
            DayOfWeek = request.DayOfWeek,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
        };

        await availabilityRuleRepository.AddAsync(rule, cancellationToken);
        await availabilityRuleRepository.SaveChangesAsync(cancellationToken);

        return rule.ToResponse();
    }
}

internal static class AvailabilityRuleMappingExtensions
{
    public static AvailabilityRuleResponse ToResponse(this AvailabilityRule rule) =>
        new(rule.Id, rule.ResourceId, rule.DayOfWeek, rule.StartTime, rule.EndTime);
}
