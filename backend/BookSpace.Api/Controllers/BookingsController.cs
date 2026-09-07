using BookSpace.Application.Bookings;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every booking/series action is plain [Authorize] - any authenticated tenant member creates/views/
// cancels their OWN bookings and recurring series. Ownership itself is enforced inside each handler
// (server-derived UserId on create, an explicit owner check on view/cancel), never by role. Approval
// actions (approve/reject/pending-approval) are the one exception: they're gated to
// Approver/TenantAdmin/SysAdmin at the route, with a further in-handler check that a plain Approver is
// actually assigned to the specific resource (see ApprovalAuthorization).
[ApiController]
[Route("bookings")]
[Authorize]
public sealed class BookingsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateBooking(CreateBookingRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(
            new CreateBookingCommandRequest(request.ResourceId, request.StartUtc, request.EndUtc, request.Quantity), cancellationToken));

    [HttpGet]
    public async Task<IActionResult> GetOwnBookings(
        int page = 1, int pageSize = 20, Guid? seriesId = null, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetOwnBookingsQueryRequest(page, pageSize, seriesId), cancellationToken));

    // cancelRemainingSeries=false (default): only this occurrence. true: this occurrence AND every
    // later still-cancellable occurrence in the same series - see docs/recurring-bookings-and-approvals.md.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> CancelBooking(Guid id, bool cancelRemainingSeries = false, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new CancelBookingCommandRequest(id, cancelRemainingSeries), cancellationToken));

    [HttpPost("series")]
    public async Task<IActionResult> CreateRecurringSeries(CreateRecurringSeriesRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(
            new CreateRecurringSeriesCommandRequest(
                request.ResourceId, request.StartDate, request.StartTime, request.EndTime, request.Frequency, request.Interval,
                request.EndDate, request.OccurrenceCount, request.Quantity),
            cancellationToken));

    [HttpGet("series/{id:guid}")]
    public async Task<IActionResult> GetRecurringSeries(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetRecurringSeriesQueryRequest(id), cancellationToken));

    [HttpGet("pending-approval")]
    [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetPendingApprovals(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetPendingApprovalsQueryRequest(), cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")]
    public async Task<IActionResult> ApproveBooking(Guid id, DecideBookingApprovalRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ApproveBookingCommandRequest(id, request.DecisionNote), cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")]
    public async Task<IActionResult> RejectBooking(Guid id, DecideBookingApprovalRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new RejectBookingCommandRequest(id, request.DecisionNote), cancellationToken));
}

public sealed record CreateBookingRequest(Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity = 1);

public sealed record CreateRecurringSeriesRequest(
    Guid ResourceId,
    DateOnly StartDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly? EndDate,
    int? OccurrenceCount,
    int Quantity = 1);

public sealed record DecideBookingApprovalRequest(string? DecisionNote);
