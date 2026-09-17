using BookSpace.Application.Mediator;
using BookSpace.Application.Resources;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every GET is plain [Authorize] - any authenticated tenant member reads resources/rules/blackouts/
// approvers/availability to decide what to book. Every POST/PUT/DELETE is admin-only, since managing
// a resource's setup is a TenantAdmin responsibility, not a Member one.
[ApiController]
[Route("resources")]
[Authorize]
public sealed class ResourcesController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> CreateResource(CreateResourceRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateResourceCommandRequest(
                request.ResourceTypeId, request.Name, request.Description, request.Capacity, request.RequiresApproval, request.TimeZoneId),
            cancellationToken);

        return CreatedAtAction(nameof(GetResource), new { id = response.Id }, response);
    }

    [HttpGet]
    public async Task<IActionResult> GetResources(
        int page = 1, int pageSize = 20, Guid? resourceTypeId = null, ResourceStatus? status = null, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetResourcesQueryRequest(page, pageSize, resourceTypeId, status), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetResource(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetResourceQueryRequest(id), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> UpdateResource(Guid id, UpdateResourceRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(
            new UpdateResourceCommandRequest(
                id, request.ResourceTypeId, request.Name, request.Description, request.Capacity,
                request.RequiresApproval, request.TimeZoneId, request.Status),
            cancellationToken));

    // Deliberately 200+body, unlike every other DELETE below (204 No Content): this is a soft-delete
    // (Status flips to Archived, the row stays), not a real removal, so unlike a true delete there IS
    // meaningful resulting state for the caller to see - discarding it just to match the others' 204
    // would throw away real information for a superficial consistency win.
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> DeleteResource(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new DeleteResourceCommandRequest(id), cancellationToken));

    [HttpGet("{id:guid}/availability-rules")]
    public async Task<IActionResult> GetAvailabilityRules(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetAvailabilityRulesQueryRequest(id), cancellationToken));

    // No single-rule GET endpoint exists to point a Location header at (only the list, above) - 201
    // without CreatedAtAction, rather than inventing a route just to satisfy the convention.
    [HttpPost("{id:guid}/availability-rules")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> CreateAvailabilityRule(Guid id, CreateAvailabilityRuleRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateAvailabilityRuleCommandRequest(id, request.DayOfWeek, request.StartTime, request.EndTime), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpDelete("{id:guid}/availability-rules/{ruleId:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> DeleteAvailabilityRule(Guid id, Guid ruleId, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteAvailabilityRuleCommandRequest(id, ruleId), cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/blackout-periods")]
    public async Task<IActionResult> GetBlackoutPeriods(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetBlackoutPeriodsQueryRequest(id), cancellationToken));

    // No single-blackout GET endpoint exists to point a Location header at (only the list, above) -
    // 201 without CreatedAtAction, rather than inventing a route just to satisfy the convention.
    [HttpPost("{id:guid}/blackout-periods")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> CreateBlackoutPeriod(Guid id, CreateBlackoutPeriodRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateBlackoutPeriodCommandRequest(id, request.StartUtc, request.EndUtc, request.Reason), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPut("{id:guid}/blackout-periods/{blackoutId:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> UpdateBlackoutPeriod(
        Guid id, Guid blackoutId, UpdateBlackoutPeriodRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(
            new UpdateBlackoutPeriodCommandRequest(id, blackoutId, request.StartUtc, request.EndUtc, request.Reason), cancellationToken));

    [HttpDelete("{id:guid}/blackout-periods/{blackoutId:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> DeleteBlackoutPeriod(Guid id, Guid blackoutId, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteBlackoutPeriodCommandRequest(id, blackoutId), cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/approvers")]
    public async Task<IActionResult> GetResourceApprovers(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetResourceApproversQueryRequest(id), cancellationToken));

    // No single-approver GET endpoint exists to point a Location header at (only the list, above) -
    // 201 without CreatedAtAction, rather than inventing a route just to satisfy the convention.
    [HttpPost("{id:guid}/approvers")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> AssignResourceApprover(Guid id, AssignResourceApproverRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new AssignResourceApproverCommandRequest(id, request.UserId), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpDelete("{id:guid}/approvers/{userId:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> RemoveResourceApprover(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await mediator.Send(new RemoveResourceApproverCommandRequest(id, userId), cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/availability")]
    public async Task<IActionResult> GetResourceAvailability(Guid id, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetResourceAvailabilityQueryRequest(id, from, to), cancellationToken));
}

public sealed record CreateResourceRequest(
    Guid ResourceTypeId, string Name, string? Description, int Capacity, bool RequiresApproval, string TimeZoneId);

public sealed record UpdateResourceRequest(
    Guid ResourceTypeId, string Name, string? Description, int Capacity, bool RequiresApproval, string TimeZoneId, ResourceStatus Status);

public sealed record CreateAvailabilityRuleRequest(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed record CreateBlackoutPeriodRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed record UpdateBlackoutPeriodRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

public sealed record AssignResourceApproverRequest(Guid UserId);
