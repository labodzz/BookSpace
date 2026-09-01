using BookSpace.Api.Logging;
using BookSpace.Application.Common;
using BookSpace.Application.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.ErrorHandling;

// Maps NotFoundException (thrown by Application-layer handlers) to a 404 ProblemDetails. Registered
// before GlobalExceptionHandler so this specific mapping wins over the generic catch-all.
public sealed class NotFoundExceptionHandler(
    ILogger<NotFoundExceptionHandler> logger,
    IProblemDetailsService problemDetailsService,
    ICorrelationIdContext correlationIdContext) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not NotFoundException notFoundException)
        {
            return false;
        }

        logger.LogInformation(
            "Not found handling {Method} {Path}: {Message}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            notFoundException.Message);

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "The requested resource was not found.",
            Detail = notFoundException.Message,
        };

        if (correlationIdContext.CorrelationId is { } correlationId)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
            httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        }

        if (notFoundException.ErrorCode is { } errorCode)
        {
            problemDetails.Extensions["errorCode"] = errorCode;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = notFoundException,
            ProblemDetails = problemDetails,
        });
    }
}
