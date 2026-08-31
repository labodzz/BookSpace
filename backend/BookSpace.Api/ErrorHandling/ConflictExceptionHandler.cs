using BookSpace.Api.Logging;
using BookSpace.Application.Common;
using BookSpace.Application.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.ErrorHandling;

// Maps ConflictException (thrown by Application-layer handlers) to a 409 ProblemDetails. Registered
// before GlobalExceptionHandler so this specific mapping wins over the generic catch-all.
public sealed class ConflictExceptionHandler(
    ILogger<ConflictExceptionHandler> logger,
    IProblemDetailsService problemDetailsService,
    ICorrelationIdContext correlationIdContext) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ConflictException conflictException)
        {
            return false;
        }

        logger.LogInformation(
            "Conflict handling {Method} {Path}: {Message}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            conflictException.Message);

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "The request could not be completed due to a conflict.",
            Detail = conflictException.Message,
        };

        if (correlationIdContext.CorrelationId is { } correlationId)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
            httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = conflictException,
            ProblemDetails = problemDetails,
        });
    }
}
