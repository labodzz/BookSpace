using BookSpace.Application.AiBooking;

namespace BookSpace.Application.Tests.AiBooking;

// The test double every automated test in this codebase uses instead of GeminiBookingIntentInterpreter -
// see the task's own "CI must not call a real paid LLM API" requirement and
// docs/ai-assisted-booking.md. Deliberately simple (one scripted response or exception per test, not a
// queue/sequence) - ProposeBookingCommandHandlerTests only ever calls InterpretAsync once per Handle().
internal sealed class FakeBookingIntentInterpreter : IBookingIntentInterpreter
{
    public LlmBookingIntent? NextResponse { get; set; }

    public Exception? NextException { get; set; }

    // True only once InterpretAsync is actually invoked - lets a test prove the interpreter was never
    // called at all (e.g. AiBookingOptions.Enabled = false, or an earlier validation already rejected the
    // request before ever reaching the interpreter).
    public bool WasCalled { get; private set; }

    public string? LastText { get; private set; }

    public IReadOnlyList<BookingIntentTurn>? LastPriorTurns { get; private set; }

    public BookingIntentContext? LastContext { get; private set; }

    public Task<LlmBookingIntent> InterpretAsync(
        string text, IReadOnlyList<BookingIntentTurn> priorTurns, BookingIntentContext context, CancellationToken cancellationToken)
    {
        WasCalled = true;
        LastText = text;
        LastPriorTurns = priorTurns;
        LastContext = context;

        if (NextException is not null)
        {
            throw NextException;
        }

        return Task.FromResult(NextResponse ?? throw new InvalidOperationException("Test must set NextResponse or NextException before calling InterpretAsync."));
    }
}
