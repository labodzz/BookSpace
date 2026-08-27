namespace BookSpace.Application.Logging;

// AsyncLocal, not HttpContext.Items - the correlation ID needs to flow through the async call chain
// for whatever is currently running, whether that's an HTTP request today or a background job later
// with no HttpContext at all. Registered as a singleton: the storage is already per-async-flow, so one
// shared instance is correct, the same pattern IHttpContextAccessor uses internally.
public sealed class CorrelationIdContext : ICorrelationIdContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId => Current.Value;

    public void Set(string correlationId) => Current.Value = correlationId;
}
