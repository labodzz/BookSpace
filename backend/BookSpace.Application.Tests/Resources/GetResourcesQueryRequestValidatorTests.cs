using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class GetResourcesQueryRequestValidatorTests
{
    private readonly GetResourcesQueryRequestValidator _sut = new();

    [Fact]
    public void Validate_WithDefaults_HasNoErrors()
    {
        var result = _sut.Validate(new GetResourcesQueryRequest());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Validate_WithInvalidPaging_HasErrors(int page, int pageSize)
    {
        var result = _sut.Validate(new GetResourcesQueryRequest(page, pageSize));

        Assert.False(result.IsValid);
    }
}
