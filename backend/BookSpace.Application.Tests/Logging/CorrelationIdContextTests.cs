using BookSpace.Application.Logging;
using Xunit;

namespace BookSpace.Application.Tests.Logging;

public sealed class CorrelationIdContextTests
{
    [Fact]
    public void CorrelationId_BeforeSet_IsNull()
    {
        var sut = new CorrelationIdContext();

        Assert.Null(sut.CorrelationId);
    }

    [Fact]
    public void Set_MakesCorrelationIdAvailableOnTheSameInstance()
    {
        var sut = new CorrelationIdContext();

        sut.Set("abc-123");

        Assert.Equal("abc-123", sut.CorrelationId);
    }

    [Fact]
    public async Task Set_FlowsAcrossAwaitsWithinTheSameAsyncContext()
    {
        var sut = new CorrelationIdContext();
        sut.Set("flows-through");

        await Task.Yield();

        Assert.Equal("flows-through", sut.CorrelationId);
    }
}
