namespace BookSpace.Application.AiBooking;

// One turn of the bounded, client-held conversation transcript - see ProposeBookingCommandRequest's own
// doc comment for why this is stateless on the backend (no persisted AI conversation) and bounded (a
// fixed max turn count/length), not an open-ended chat log.
public sealed record BookingIntentTurn(BookingIntentTurnRole Role, string Text);

public enum BookingIntentTurnRole
{
    User,
    Assistant,
}

// Everything the interpreter is told about "now" and "what this tenant actually has" - see
// docs/ai-assisted-booking.md ("Reference time zone") for why ReferenceDate/ReferenceTime/
// ReferenceTimeZoneId are computed by the BACKEND (via TimeProvider + Tenant.DefaultTimeZoneId), never
// left for the model to assume, and why ResourceTypeNames is the one piece of tenant data sent at all -
// enough to ground "laboratorija"/"lab" against this tenant's REAL catalog without ever sending a
// resource's Description, Capacity, approval configuration, or any other tenant's data.
public sealed record BookingIntentContext(
    IReadOnlyList<string> ResourceTypeNames,
    DateOnly ReferenceDate,
    TimeOnly ReferenceTime,
    string ReferenceTimeZoneId,
    DayOfWeek ReferenceDayOfWeek);

// The one shared, bilingual structured schema every provider (today: the one real OpenAI implementation;
// in tests: FakeBookingIntentInterpreter) must produce - see docs/ai-assisted-booking.md for the full
// field-by-field contract. Every field is nullable/optional by design: this is untrusted model output
// (see ProposeBookingCommandRequest's own validation of every field), never a type the rest of the
// backend treats as already correct just because it deserialized.
//
// Status "NeedsClarification" with a non-empty ClarificationQuestion is itself a valid, complete
// response - it means the model determined up front that it cannot propose a candidate (e.g. the request
// mentioned no resource at all), as opposed to leaving individual fields null and expecting the backend
// to notice. Both paths are handled: ProposeBookingCommandHandler treats Status=NeedsClarification AND
// "the fields it did fill in don't resolve to a bookable candidate" identically.
public sealed record LlmBookingIntent(
    string? Language,
    string? Status,
    string? ClarificationQuestion,
    string? ResourceTypeHint,
    string? ResourceNameHint,
    int? PartySize,
    int? Quantity,
    string? Date,
    bool? DateIsRelativeExpression,
    string? StartTime,
    string? EndTime,
    int? DurationMinutes);

// Thrown by an IBookingIntentInterpreter implementation for any failure that means "no usable response
// was obtained at all" - a network error, a timeout, a non-success HTTP status, or a response that could
// not even be parsed as the expected JSON shape. Deliberately NOT thrown for "the model responded, but
// left required fields null/ambiguous" - that is a normal, expected outcome (see LlmBookingIntent's own
// doc comment), not a provider fault, and is handled by ProposeBookingCommandHandler's own field
// validation instead. Message is always safe to log (no raw prompt/response content, no secrets) - see
// the interface's own doc comment.
public sealed class BookingIntentInterpretationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// Small, injectable abstraction over "ask an LLM to turn free text into structured booking intent" - see
// docs/ai-assisted-booking.md for the full design. Exactly one real implementation exists
// (GeminiBookingIntentInterpreter, BookSpace.Infrastructure) plus a test double
// (FakeBookingIntentInterpreter) - this interface is what makes ProposeBookingCommandHandler unit-
// testable without ever making a real HTTP call, and is the one, explicit seam a future second provider
// would implement against, never a reason by itself to add one speculatively.
//
// Implementations must respect cancellationToken and must never retry internally - bounded behavior on
// failure (one attempt, one bounded timeout) is ProposeBookingCommandHandler's own responsibility via the
// CancellationToken it passes in, not something an implementation should second-guess by retrying behind
// the caller's back.
public interface IBookingIntentInterpreter
{
    Task<LlmBookingIntent> InterpretAsync(
        string text, IReadOnlyList<BookingIntentTurn> priorTurns, BookingIntentContext context, CancellationToken cancellationToken);
}
