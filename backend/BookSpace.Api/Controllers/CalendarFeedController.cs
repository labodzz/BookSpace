using BookSpace.Application.Calendar;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Anonymous by design: an external calendar client polling this URL never presents a BookSpace JWT - the
// query-string token itself IS this endpoint's credential (see GetCalendarFeedQueryHandler, which enforces
// user/tenant isolation and the active-user/active-tenant checks the global JWT pipeline would otherwise
// provide). See docs/calendar-subscription-feed.md.
[ApiController]
[Route("api/calendar-feed")]
[AllowAnonymous]
public sealed class CalendarFeedController(IMediator mediator) : ControllerBase
{
    // token is a query-string parameter, never a route segment - see GetCalendarFeedQueryRequest for why
    // (keeps it out of Serilog's default per-request summary line, which logs RequestPath but never
    // QueryString). NoStore so neither the browser nor any shared/intermediate cache ever retains a
    // response keyed by this sensitive URL - see docs/calendar-subscription-feed.md ("Caching").
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetFeed(string? token, CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new GetCalendarFeedQueryRequest(token), cancellationToken);
        return Content(response.IcsContent, "text/calendar; charset=utf-8");
    }
}
