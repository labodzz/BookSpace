using BookSpace.Application.AiBooking;

namespace BookSpace.Api.Tests.AiBooking;

// The test double swapped in for the real GeminiBookingIntentInterpreter via WithWebHostBuilder/
// ConfigureTestServices - see AiBookingsEndpointsTests. Registered as a Singleton instance (not a type),
// so a test can configure NextResponse before the HTTP call and the exact same instance is what the real
// mediator pipeline resolves - see the "CI must not call a real paid LLM API" requirement.
internal sealed class FakeBookingIntentInterpreter : IBookingIntentInterpreter
{
    public LlmBookingIntent? NextResponse { get; set; }

    public Task<LlmBookingIntent> InterpretAsync(
        string text, IReadOnlyList<BookingIntentTurn> priorTurns, BookingIntentContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NextResponse ?? throw new InvalidOperationException("Test must set NextResponse before calling the propose endpoint."));
}
