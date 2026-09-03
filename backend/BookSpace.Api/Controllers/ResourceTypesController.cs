using BookSpace.Application.Mediator;
using BookSpace.Application.ResourceTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every GET is plain [Authorize] - any authenticated tenant member reads the type catalog to pick
// one when creating a resource. Every POST/PUT/DELETE is admin-only, same convention as
// ResourcesController.
[ApiController]
[Route("resource-types")]
[Authorize]
public sealed class ResourceTypesController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> CreateResourceType(CreateResourceTypeRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new CreateResourceTypeCommandRequest(request.Name), cancellationToken));

    [HttpGet]
    public async Task<IActionResult> GetResourceTypes(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetResourceTypesQueryRequest(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetResourceType(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetResourceTypeQueryRequest(id), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> UpdateResourceType(Guid id, UpdateResourceTypeRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new UpdateResourceTypeCommandRequest(id, request.Name), cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> DeleteResourceType(Guid id, CancellationToken cancellationToken)
    {
        await mediator.Send(new DeleteResourceTypeCommandRequest(id), cancellationToken);
        return NoContent();
    }
}

public sealed record CreateResourceTypeRequest(string Name);

public sealed record UpdateResourceTypeRequest(string Name);
