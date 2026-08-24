using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("users")]
[Authorize]
public sealed class UsersController(BookSpaceDbContext dbContext, ICurrentUserContext currentUserContext) : ControllerBase
{
    [HttpGet("me")]
    public IActionResult GetMe() => Ok(new
    {
        userId = currentUserContext.UserId,
        tenantId = currentUserContext.TenantId,
        roles = currentUserContext.Roles,
    });

    // No .Where(u => u.TenantId == ...) here on purpose - the DbContext's global query filter
    // already restricts this to the caller's tenant. That's the point of the structural approach:
    // this endpoint cannot leak another tenant's users even if someone forgets to add a filter.
    [HttpGet]
    [Authorize(Roles = "TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .OrderBy(user => user.Email)
            .Select(user => new { user.Id, user.FirstName, user.LastName, user.Email, user.TenantId })
            .ToListAsync(cancellationToken);

        return Ok(users);
    }
}
