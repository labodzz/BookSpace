using BookSpace.Application.Calendar;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every action manages the CALLER's own calendar subscription only - there is no concept of managing
// someone else's (see Create/Regenerate/RevokeCalendarSubscriptionCommandHandler, none of which accept a
// UserId/TenantId from the request). Plain [Authorize] (inherited from the global fallback policy), the
// same as BookingsController's own ordinary, non-role-gated actions. See docs/calendar-subscription-feed.md.
[ApiController]
[Route("api/calendar-subscriptions")]
[Authorize]
public sealed class CalendarSubscriptionsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new GetCalendarSubscriptionStatusQueryRequest(), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new CreateCalendarSubscriptionCommandRequest(), cancellationToken));

    [HttpPost("regenerate")]
    public async Task<IActionResult> Regenerate(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new RegenerateCalendarSubscriptionCommandRequest(), cancellationToken));

    [HttpDelete]
    public async Task<IActionResult> Revoke(CancellationToken cancellationToken)
    {
        await mediator.Send(new RevokeCalendarSubscriptionCommandRequest(), cancellationToken);
        return NoContent();
    }
}
