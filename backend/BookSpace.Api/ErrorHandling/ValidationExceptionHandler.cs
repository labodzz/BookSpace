using BookSpace.Api.Logging;
using BookSpace.Application.Logging;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.ErrorHandling;

// Catches the ValidationException thrown by the mediator's ValidationBehavior before a handler ever
// runs, and turns it into a clean 400 with field-level errors instead of letting it surface as a raw
// unhandled exception. Registered as an IExceptionHandler so it plugs into the same
// UseExceptionHandler() pipeline as any other exception-to-response mapping.
public sealed class ValidationExceptionHandler(
    ILogger<ValidationExceptionHandler> logger,
    IProblemDetailsService problemDetailsService,
    ICorrelationIdContext correlationIdContext) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
        {
            return false;
        }

        var errors = validationException.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray());

        // Information, not Error - a validation failure is an expected outcome of untrusted input,
        // not a bug. Only which fields failed and how many is logged, never the submitted values.
        logger.LogInformation(
            "Request validation failed for {Method} {Path} with {ErrorCount} invalid field(s): {InvalidFields}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            errors.Count,
            errors.Keys);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problemDetails = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
        };

        if (correlationIdContext.CorrelationId is { } correlationId)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
            // UseExceptionHandler resets the response (including headers) before an IExceptionHandler
            // runs, so CorrelationIdMiddleware's header write earlier in the pipeline doesn't survive
            // an exception - it has to be set again here for the header to reach the client at all.
            httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = validationException,
            ProblemDetails = problemDetails,
        });
    }
}
