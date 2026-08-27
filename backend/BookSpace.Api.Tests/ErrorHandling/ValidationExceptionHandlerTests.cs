using BookSpace.Api.ErrorHandling;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BookSpace.Api.Tests.ErrorHandling;

public sealed class ValidationExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_WithValidationException_WritesA400WithGroupedFieldErrors()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ValidationExceptionHandler(problemDetailsService);
        var httpContext = new DefaultHttpContext();
        var exception = new ValidationException([
            new ValidationFailure("Email", "'Email' must not be empty."),
            new ValidationFailure("Email", "'Email' is not a valid email address."),
            new ValidationFailure("Password", "'Password' must not be empty."),
        ]);

        var handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(problemDetailsService.CapturedContext!.ProblemDetails);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal(2, problemDetails.Errors["Email"].Length);
        Assert.Single(problemDetails.Errors["Password"]);
    }

    [Fact]
    public async Task TryHandleAsync_WithAnUnrelatedException_ReturnsFalseWithoutWritingAResponse()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ValidationExceptionHandler(problemDetailsService);
        var httpContext = new DefaultHttpContext();

        var handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.False(handled);
        Assert.Null(problemDetailsService.CapturedContext);
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? CapturedContext { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            CapturedContext = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            CapturedContext = context;
            return ValueTask.CompletedTask;
        }
    }
}
