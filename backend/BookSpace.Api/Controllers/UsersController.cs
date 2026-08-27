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
        Ok(await mediator.Send(new GetCurrentUserQuery(), cancellationToken));

    // No tenant filter here on purpose - GetUsersQueryHandler reads through IUserRepository, whose
    // implementation relies on the DbContext's global query filter. That's the point of the
    // structural approach: this endpoint cannot leak another tenant's users even if someone forgets
    // to add a filter.
    [HttpGet]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetUsersQuery(), cancellationToken));
}
