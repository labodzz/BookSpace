using BookSpace.Application.Resources;
using Xunit;

namespace BookSpace.Application.Tests.Resources;

public sealed class AssignResourceApproverCommandRequestValidatorTests
{
    private readonly AssignResourceApproverCommandRequestValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _sut.Validate(new AssignResourceApproverCommandRequest(Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyResourceId_HasErrors()
    {
        var result = _sut.Validate(new AssignResourceApproverCommandRequest(Guid.Empty, Guid.NewGuid()));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WithEmptyUserId_HasErrors()
    {
        var result = _sut.Validate(new AssignResourceApproverCommandRequest(Guid.NewGuid(), Guid.Empty));

        Assert.False(result.IsValid);
    }
}
