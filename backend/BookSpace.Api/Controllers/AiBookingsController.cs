using BookSpace.Application.AiBooking;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BookSpace.Api.Controllers;

// AI-assisted booking (WP-9) - see docs/ai-assisted-booking.md. Plain [Authorize] (any authenticated
// tenant member, same as BookingsController) - ProposeBookingCommandHandler itself rejects the global
// SysAdmin via TenantMembershipGuard, the same way every other tenant-member-only action in this codebase
// does, rather than gating the route by role.
//
// Deliberately only ONE action: there is no "/confirm" endpoint here at all. Confirming a proposal means
// calling the EXISTING POST /api/bookings (BookingsController.CreateBooking) with the candidate's own
// ResourceId/StartUtc/EndUtc/Quantity - see ProposeBookingCommandRequest's own doc comment for why that is
// the entire confirmation mechanism, not a second, parallel one.
[ApiController]
[Route("api/ai-bookings")]
[Authorize]
public sealed class AiBookingsController(IMediator mediator) : ControllerBase
{
    // Rate-limited (see Program.cs's "ai-booking" policy) - every call reaches a paid external API, unlike
    // the rest of this controller's sibling endpoints elsewhere in the app.
    [HttpPost("propose")]
    [EnableRateLimiting("ai-booking")]
    public async Task<IActionResult> Propose(ProposeBookingRequest request, CancellationToken cancellationToken)
    {
        var priorTurns = request.PriorTurns?.Select(turn => new ProposeBookingTurnRequest(turn.Role, turn.Text)).ToList();
        var response = await mediator.Send(new ProposeBookingCommandRequest(request.Text, priorTurns), cancellationToken);
        return Ok(response);
    }
}

public sealed record ProposeBookingRequest(string Text, IReadOnlyList<ProposeBookingConversationTurnRequest>? PriorTurns);

public sealed record ProposeBookingConversationTurnRequest(string Role, string Text);
