using BookSpace.Application.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.ErrorHandling;

// Catch-all safety net: anything not already mapped by a more specific IExceptionHandler (like
// ValidationExceptionHandler) ends up here. IExceptionHandlers run in registration order and stop at
// the first one that returns true, so this one is registered last in Program.cs. Logs the full
// exception - with stack trace - exactly once, here at the boundary, then writes a generic
// ProblemDetails response that never includes the exception's message or stack trace: the client
// gets a correlation ID to hand back for support, not internal details.
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService,
    ICorrelationIdContext correlationIdContext) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(
            exception,
            "Unhandled exception handling {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        };

        if (correlationIdContext.CorrelationId is { } correlationId)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
