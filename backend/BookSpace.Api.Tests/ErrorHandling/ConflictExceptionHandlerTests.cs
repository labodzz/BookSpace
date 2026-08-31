using BookSpace.Api.ErrorHandling;
using BookSpace.Api.Logging;
using BookSpace.Application.Common;
using BookSpace.Application.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Api.Tests.ErrorHandling;

public sealed class ConflictExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_WithConflictException_WritesA409WithDetailAndCorrelationId()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ConflictExceptionHandler(
            NullLogger<ConflictExceptionHandler>.Instance, problemDetailsService, new FakeCorrelationIdContext("correlation-xyz"));
        var httpContext = new DefaultHttpContext();
        var exception = new ConflictException("A resource named 'Conference Room A' already exists.");

        var handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        var problemDetails = problemDetailsService.CapturedContext!.ProblemDetails;
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal(exception.Message, problemDetails.Detail);
        Assert.Equal("correlation-xyz", problemDetails.Extensions["correlationId"]);
        Assert.Equal("correlation-xyz", httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task TryHandleAsync_WithErrorCode_IncludesItInTheExtensions()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ConflictExceptionHandler(
            NullLogger<ConflictExceptionHandler>.Instance, problemDetailsService, new FakeCorrelationIdContext(null));
        var httpContext = new DefaultHttpContext();
        var exception = new ConflictException("conflict", "Resource.NameConflict");

        await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        var problemDetails = problemDetailsService.CapturedContext!.ProblemDetails;
        Assert.Equal("Resource.NameConflict", problemDetails.Extensions["errorCode"]);
    }

    [Fact]
    public async Task TryHandleAsync_WithNoErrorCode_OmitsTheExtension()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ConflictExceptionHandler(
            NullLogger<ConflictExceptionHandler>.Instance, problemDetailsService, new FakeCorrelationIdContext(null));
        var httpContext = new DefaultHttpContext();

        await sut.TryHandleAsync(httpContext, new ConflictException("conflict"), CancellationToken.None);

        var problemDetails = problemDetailsService.CapturedContext!.ProblemDetails;
        Assert.False(problemDetails.Extensions.ContainsKey("errorCode"));
    }

    [Fact]
    public async Task TryHandleAsync_WithNoCorrelationIdSet_OmitsTheExtension()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ConflictExceptionHandler(
            NullLogger<ConflictExceptionHandler>.Instance, problemDetailsService, new FakeCorrelationIdContext(null));
        var httpContext = new DefaultHttpContext();

        await sut.TryHandleAsync(httpContext, new ConflictException("conflict"), CancellationToken.None);

        var problemDetails = problemDetailsService.CapturedContext!.ProblemDetails;
        Assert.False(problemDetails.Extensions.ContainsKey("correlationId"));
        Assert.False(httpContext.Response.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task TryHandleAsync_WithAnUnrelatedException_ReturnsFalseWithoutWritingAResponse()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var sut = new ConflictExceptionHandler(
            NullLogger<ConflictExceptionHandler>.Instance, problemDetailsService, new FakeCorrelationIdContext(null));
        var httpContext = new DefaultHttpContext();

        var handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.False(handled);
        Assert.Null(problemDetailsService.CapturedContext);
    }

    private sealed class FakeCorrelationIdContext(string? correlationId) : ICorrelationIdContext
    {
        public string? CorrelationId { get; private set; } = correlationId;

        public void Set(string correlationId) => CorrelationId = correlationId;
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
