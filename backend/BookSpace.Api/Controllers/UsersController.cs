using BookSpace.Application.Mediator;
using BookSpace.Application.Users;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("api/users")]
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
    public async Task<IActionResult> GetUsers(
        int page = 1,
        int pageSize = 20,
        string? search = null,
        string? role = null,
        // [ApiController]'s binding-source inference does not reliably bind a repeated-key query array
        // (?ids=a&ids=b) onto a plain Guid[] parameter alongside several other simple-typed parameters -
        // confirmed live, it silently comes through as null. Explicit [FromQuery] fixes it.
        [FromQuery] Guid[]? ids = null,
        UserStatus? status = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetUsersQueryRequest(page, pageSize, search, role, ids, status), cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetUserQueryRequest(id), cancellationToken));

    // Returns the raw invitation token once, in this response only - see InviteUserResponse. No email-
    // delivery infrastructure exists in this codebase yet, so the authorized admin is responsible for
    // relaying this link to the invitee through whatever channel they already use.
    [HttpPost("invitations")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> InviteUser(InviteUserRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new InviteUserCommandRequest(request.Email, request.FirstName, request.LastName, request.Roles), cancellationToken);
        return CreatedAtAction(nameof(GetUser), new { id = response.UserId }, response);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> UpdateUser(Guid id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new UpdateUserCommandRequest(id, request.FirstName, request.LastName), cancellationToken));

    // A soft "delete" (Status -> Inactive), same convention as DELETE /resources/{id} - see
    // DeactivateUserCommandHandler. Returns the updated user, not 204, so the caller sees the new
    // status without a follow-up GET.
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> DeactivateUser(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new DeactivateUserCommandRequest(id), cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> ReactivateUser(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ReactivateUserCommandRequest(id), cancellationToken));

    // No single-role GET endpoint exists to point a Location header at - 201 without CreatedAtAction,
    // same convention AssignResourceApprover already uses for the identical situation.
    [HttpPost("{id:guid}/roles")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> AssignUserRole(Guid id, AssignUserRoleRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new AssignUserRoleCommandRequest(id, request.Role), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpDelete("{id:guid}/roles/{role}")]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> RemoveUserRole(Guid id, string role, CancellationToken cancellationToken)
    {
        await mediator.Send(new RemoveUserRoleCommandRequest(id, role), cancellationToken);
        return NoContent();
    }
}

public sealed record InviteUserRequest(string Email, string FirstName, string LastName, IReadOnlyList<string> Roles);

public sealed record UpdateUserRequest(string FirstName, string LastName);

public sealed record AssignUserRoleRequest(string Role);
