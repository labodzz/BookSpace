using BookSpace.Application.Mediator;
using BookSpace.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("users")]
[Authorize]
public sealed class UsersController(IMediator mediator) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetCurrentUserQueryRequest(), cancellationToken));

    // No tenant filter here on purpose - GetUsersQueryRequestHandler reads through IUserRepository,
    // whose implementation relies on the DbContext's global query filter. That's the point of the
    // structural approach: this endpoint cannot leak another tenant's users even if someone forgets
    // to add a filter.
    [HttpGet]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetUsers(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetUsersQueryRequest(page, pageSize), cancellationToken));
}
