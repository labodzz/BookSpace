using BookSpace.Application.Logging;
using Serilog.Context;

namespace BookSpace.Api.Logging;

// Reads X-Correlation-Id from the request if the caller supplied one (so a call chain across
// services can share one ID), otherwise mints a new one. Pushes it onto Serilog's LogContext so
// every log written for the rest of this request - including from deep inside the mediator pipeline
// or the global exception handler - carries it as a structured property without having to pass it
// through every method signature, and echoes it back on the response so the caller can correlate too.
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext httpContext, ICorrelationIdContext correlationIdContext)
    {
        var correlationId = httpContext.Request.Headers.TryGetValue(HeaderName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue)
            ? headerValue.ToString()
            : Guid.NewGuid().ToString();

        correlationIdContext.Set(correlationId);
        httpContext.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(httpContext);
        }
    }
}
