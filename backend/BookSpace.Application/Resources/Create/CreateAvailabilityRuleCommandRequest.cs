using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record CreateAvailabilityRuleCommandRequest(Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime)
    : IRequest<CreateAvailabilityRuleResponse>;

public sealed record CreateAvailabilityRuleResponse(Guid Id, Guid ResourceId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed class CreateAvailabilityRuleCommandRequestValidator : AbstractValidator<CreateAvailabilityRuleCommandRequest>
{
    public CreateAvailabilityRuleCommandRequestValidator()
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
    ICurrentUserContext currentUserContext) : IRequestHandler<CreateAvailabilityRuleCommandRequest, CreateAvailabilityRuleResponse>
{
    public async Task<CreateAvailabilityRuleResponse> Handle(CreateAvailabilityRuleCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.");

        // Archived is terminal - configuring a schedule for a resource that's effectively deleted from
        // the caller's point of view would silently keep it half-alive. Maintenance is deliberately NOT
        // blocked here: it's temporary, and an admin may well want to adjust the schedule while it lasts.
        if (resource.Status == ResourceStatus.Archived)
        {
            throw new ConflictException($"Resource {request.ResourceId} is archived and cannot be modified.");
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

        return new CreateAvailabilityRuleResponse(rule.Id, rule.ResourceId, rule.DayOfWeek, rule.StartTime, rule.EndTime);
    }
}
