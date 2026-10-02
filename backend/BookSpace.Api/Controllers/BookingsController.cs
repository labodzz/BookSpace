using BookSpace.Application.Bookings;
using BookSpace.Application.Mediator;
using BookSpace.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every booking/series action is plain [Authorize] - any authenticated tenant member creates/views/
// cancels their OWN bookings and recurring series. Ownership itself is enforced inside each handler
// (server-derived UserId on create, an explicit owner check on view/cancel), not gated by role at the
// route. Two actions widen that in-handler check instead of gating the route: cancel additionally lets a
// TenantAdmin/SysAdmin cancel someone ELSE's booking (see docs/open-questions.md), and approve/reject
// (below) are gated to Approver/TenantAdmin/SysAdmin at the route, with a further in-handler check that a
// plain Approver is actually assigned to the specific resource (see ApprovalAuthorization).
[ApiController]
[Route("api/bookings")]
[Authorize]
public sealed class BookingsController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateBooking(CreateBookingRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateBookingCommandRequest(request.ResourceId, request.StartUtc, request.EndUtc, request.Quantity), cancellationToken);

        // No single-booking GET endpoint exists to point a Location header at (only the list, above) -
        // 201 without CreatedAtAction, rather than inventing a route just to satisfy the convention.
        return StatusCode(StatusCodes.Status201Created, response);
    }

    // fromUtc/toUtc are optional and exist for the frontend calendar, which needs to ask for just the
    // currently-visible date range rather than paging through a user's entire booking history.
    [HttpGet]
    public async Task<IActionResult> GetOwnBookings(
        int page = 1, int pageSize = 20, Guid? seriesId = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetOwnBookingsQueryRequest(page, pageSize, seriesId, fromUtc, toUtc), cancellationToken));

    // cancelRemainingSeries=false (default): only this occurrence. true: this occurrence AND every
    // later still-cancellable occurrence in the same series - see docs/recurring-bookings-and-approvals.md.
    // reason is optional; it only really matters when a TenantAdmin/SysAdmin cancels someone ELSE's
    // booking (the handler records who+why so it isn't a black box in the owner's history) - see
    // docs/open-questions.md, "TenantAdmin Cancellation of Another User's Booking".
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> CancelBooking(
        Guid id, bool cancelRemainingSeries = false, string? reason = null, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new CancelBookingCommandRequest(id, cancelRemainingSeries, reason), cancellationToken));

    [HttpPost("series")]
    public async Task<IActionResult> CreateRecurringSeries(CreateRecurringSeriesRequest request, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateRecurringSeriesCommandRequest(
                request.ResourceId, request.StartDate, request.StartTime, request.EndTime, request.Frequency, request.Interval,
                request.EndDate, request.OccurrenceCount, request.Quantity),
            cancellationToken);

        return CreatedAtAction(nameof(GetRecurringSeries), new { id = response.SeriesId }, response);
    }

    [HttpGet("series/{id:guid}")]
    public async Task<IActionResult> GetRecurringSeries(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetRecurringSeriesQueryRequest(id), cancellationToken));

    [HttpGet("pending-approval")]
    [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")]
    public async Task<IActionResult> GetPendingApprovals(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetPendingApprovalsQueryRequest(), cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Approver,TenantAdmin,SysAdmin")]
    public async Task<IActionResult> ApproveBooking(Guid id, ApproveBookingRequest request, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ApproveBookingCommandRequest(id, request.DecisionNote, request.ApproveRemainingSeries), cancellationToken));

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

// ApproveRemainingSeries=false (default): only this occurrence. true: this occurrence AND every other
// still-Pending occurrence in the same recurring series - see ApproveBookingCommandRequest.
public sealed record ApproveBookingRequest(string? DecisionNote, bool ApproveRemainingSeries = false);

public sealed record DecideBookingApprovalRequest(string? DecisionNote);
