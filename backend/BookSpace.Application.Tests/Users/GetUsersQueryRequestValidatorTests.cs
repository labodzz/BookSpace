using BookSpace.Application.Users;
using Xunit;

namespace BookSpace.Application.Tests.Users;

public sealed class GetUsersQueryRequestValidatorTests
{
    private readonly GetUsersQueryRequestValidator _sut = new();

    [Fact]
    public void Validate_WithDefaultPaging_HasNoErrors()
    {
        var result = _sut.Validate(new GetUsersQueryRequest());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageBelowOne_HasErrors()
    {
        var result = _sut.Validate(new GetUsersQueryRequest(Page: 0));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeAboveOneHundred_HasErrors()
    {
        var result = _sut.Validate(new GetUsersQueryRequest(PageSize: 101));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeBelowOne_HasErrors()
    {
        var result = _sut.Validate(new GetUsersQueryRequest(PageSize: 0));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithPageSizeOfExactlyOneHundred_HasNoErrors()
    {
        var result = _sut.Validate(new GetUsersQueryRequest(PageSize: 100));

        Assert.True(result.IsValid);
    }
}
