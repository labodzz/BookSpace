using BookSpace.Application.Mediator;
using BookSpace.Application.Tenants;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// SysAdmin only, for every action here - see docs/tenant-onboarding.md. Unlike every
// [Authorize(Roles = "TenantAdmin")] controller elsewhere in this codebase, these are platform tenant-
// lifecycle operations, not a tenant's own business functions - TenantAdmin never gets access here, not
// even to its own tenant (it reads/edits its own tenant's business data through other controllers
// instead; see docs/tenant-onboarding.md, "Permissions matrix"). A brand-new tenant has no TenantAdmin
// yet for CreateTenant specifically anyway - onboarding one is the whole point of that one action.
[ApiController]
[Route("api/tenants")]
[Authorize(Roles = "SysAdmin")]
public sealed class TenantsController(IMediator mediator) : ControllerBase
{
    // Returns the newly created tenant and its first administrator's id/email - never the invitation
    // token or its URL (see CreateTenantResponse). The actual email is only enqueued here, not sent -
    // see docs/tenant-onboarding.md ("What 'created' actually means") for why the response must never be
    // read as "the invitation email has been sent."
    [HttpPost]
    public async Task<IActionResult> CreateTenant(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateTenantCommandRequest(request.Name, request.DefaultTimeZoneId, request.AdminFirstName, request.AdminLastName, request.AdminEmail),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpGet]
    public async Task<IActionResult> GetTenants(
        int page = 1, int pageSize = 20, string? search = null, TenantStatus? status = null, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetTenantsQueryRequest(page, pageSize, search, status), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTenant(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetTenantQueryRequest(id), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTenant(Guid id, UpdateTenantRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new UpdateTenantCommandRequest(id, request.Name), cancellationToken));

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> SuspendTenant(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new SuspendTenantCommandRequest(id), cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> ReactivateTenant(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ReactivateTenantCommandRequest(id), cancellationToken));

    // Soft-delete - see DeleteTenantCommandRequest. Body carries the typed confirmation name rather than
    // a query string: it can be arbitrarily long user input (a real tenant name), which belongs in a body,
    // not a URL.
    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> DeleteTenant(Guid id, DeleteTenantRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new DeleteTenantCommandRequest(id, request.ConfirmName), cancellationToken));

    // 200 with the resend result, not 201/CreatedAtAction(GetTenant, ...) - a resend doesn't create a
    // new tenant resource, it mutates an existing one's invitation state, so there's no new resource
    // location to point a Location header at.
    [HttpPost("{tenantId:guid}/users/{userId:guid}/resend-invitation")]
    public async Task<IActionResult> ResendAdminInvitation(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ResendTenantAdminInvitationCommandRequest(tenantId, userId), cancellationToken));

    [HttpDelete("{tenantId:guid}/users/{userId:guid}/invitation")]
    public async Task<IActionResult> RevokeAdminInvitation(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await mediator.Send(new RevokeTenantAdminInvitationCommandRequest(tenantId, userId), cancellationToken);
        return NoContent();
    }

    // Platform recovery: invite a NEW person as (an additional or replacement) TenantAdmin for an
    // existing tenant - see InviteReplacementTenantAdminCommandRequest for why this is distinct from both
    // CreateTenant (which also creates the Tenant itself) and UsersController's own InviteUser (which is
    // TenantAdmin-only, scoped to the caller's own tenant, and never emails anything).
    [HttpPost("{tenantId:guid}/admin-invitations")]
    public async Task<IActionResult> InviteReplacementTenantAdmin(
        Guid tenantId, InviteReplacementTenantAdminRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new InviteReplacementTenantAdminCommandRequest(tenantId, request.FirstName, request.LastName, request.Email), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }
}

public sealed record CreateTenantRequest(string Name, string DefaultTimeZoneId, string AdminFirstName, string AdminLastName, string AdminEmail);

public sealed record UpdateTenantRequest(string Name);

public sealed record DeleteTenantRequest(string ConfirmName);

public sealed record InviteReplacementTenantAdminRequest(string FirstName, string LastName, string Email);
