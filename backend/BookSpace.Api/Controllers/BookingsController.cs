using BookSpace.Application.Bookings;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Every action is plain [Authorize] - any authenticated tenant member creates/views/cancels their OWN
// bookings. There is no admin-only action here (unlike ResourcesController): booking your own slot
// isn't a TenantAdmin responsibility the way managing a resource's setup is. Ownership itself is
// enforced inside each handler (server-derived UserId on create, an explicit owner check on cancel),
// never by role.
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
    public async Task<IActionResult> GetOwnBookings(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new GetOwnBookingsQueryRequest(page, pageSize), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> CancelBooking(Guid id, CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new CancelBookingCommandRequest(id), cancellationToken));
}

public sealed record CreateBookingRequest(Guid ResourceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity = 1);
