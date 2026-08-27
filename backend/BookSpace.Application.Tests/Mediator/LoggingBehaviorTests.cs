using BookSpace.Application.Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Application.Tests.Mediator;

public sealed class LoggingBehaviorTests
{
    private sealed record TestRequest(string Value) : IRequest<string>;

    [Fact]
    public async Task Handle_InvokesNextAndReturnsItsResult()
    {
        var sut = new LoggingBehavior<TestRequest, string>(NullLogger<LoggingBehavior<TestRequest, string>>.Instance);
        var nextCalled = false;

        var result = await sut.Handle(new TestRequest("value"), Next, CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Equal("handled", result);

        Task<string> Next()
        {
            nextCalled = true;
            return Task.FromResult("handled");
        }
    }

    [Fact]
    public async Task Handle_WhenNextThrows_PropagatesTheException()
    {
        var sut = new LoggingBehavior<TestRequest, string>(NullLogger<LoggingBehavior<TestRequest, string>>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new TestRequest("value"), () => throw new InvalidOperationException("boom"), CancellationToken.None));
    }
}
