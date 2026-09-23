using BookSpace.Application.Auth;
using BookSpace.Application.Common;
using BookSpace.Application.Mediator;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using FluentValidation;

namespace BookSpace.Application.Resources;

public sealed record AssignResourceApproverCommandRequest(Guid ResourceId, Guid UserId) : IRequest<AssignResourceApproverResponse>;

public sealed record AssignResourceApproverResponse(Guid Id, Guid ResourceId, Guid UserId);

public sealed class AssignResourceApproverCommandRequestValidator : AbstractValidator<AssignResourceApproverCommandRequest>
{
    public AssignResourceApproverCommandRequestValidator()
    {
        RuleFor(command => command.ResourceId).NotEmpty();
        RuleFor(command => command.UserId).NotEmpty();
    }
}

// A cross-tenant UserId is already invisible via IUserRepository's tenant query filter, so the
// "user must exist" lookup below doubles as a tenant check for free - no separate tenant guard needed.
public sealed class AssignResourceApproverCommandHandler(
    IResourceApproverRepository resourceApproverRepository,
    IResourceRepository resourceRepository,
    IUserRepository userRepository,
    ICurrentUserContext currentUserContext) : IRequestHandler<AssignResourceApproverCommandRequest, AssignResourceApproverResponse>
{
    public async Task<AssignResourceApproverResponse> Handle(AssignResourceApproverCommandRequest request, CancellationToken cancellationToken)
    {
        var resource = await resourceRepository.FindByIdAsync(request.ResourceId, cancellationToken)
            ?? throw new NotFoundException($"Resource {request.ResourceId} was not found.", ErrorCodes.ResourceNotFound);

        ResourceGuard.EnsureNotArchived(resource);

        if (await userRepository.FindByIdAsync(request.UserId, cancellationToken) is null)
        {
            throw new NotFoundException($"User {request.UserId} was not found.", ErrorCodes.UserNotFound);
        }

        // A per-resource approver assignment is meaningless for a user who can never actually decide an
        // approval - ApprovalAuthorization only ever lets a plain Approver, TenantAdmin, or SysAdmin
        // through. Without this check, assigning e.g. a plain Member as an approver would silently
        // create a ResourceApprover row that ApprovalAuthorization would still reject at decision time,
        // leaving the resource's approval queue with an assignee who can never actually approve anything.
        var targetRoles = await userRepository.GetRolesAsync(request.UserId, cancellationToken);
        if (!targetRoles.Contains("Approver") && !targetRoles.Contains("TenantAdmin") && !targetRoles.Contains("SysAdmin"))
        {
            throw new ConflictException(
                $"User {request.UserId} does not hold a role that can approve bookings (Approver, TenantAdmin, or SysAdmin).",
                ErrorCodes.ResourceApproverRoleRequired);
        }

        if (await resourceApproverRepository.FindByResourceAndUserAsync(request.ResourceId, request.UserId, cancellationToken) is not null)
        {
            throw new ConflictException(
                $"User {request.UserId} is already an approver for resource {request.ResourceId}.",
                ErrorCodes.ResourceApproverConflict);
        }

        var approver = new ResourceApprover
        {
            Id = Guid.NewGuid(),
            TenantId = currentUserContext.TenantId!.Value,
            ResourceId = request.ResourceId,
            UserId = request.UserId,
        };

        await resourceApproverRepository.AddAsync(approver, cancellationToken);
        await resourceApproverRepository.SaveChangesAsync(cancellationToken);

        return new AssignResourceApproverResponse(approver.Id, approver.ResourceId, approver.UserId);
    }
}
