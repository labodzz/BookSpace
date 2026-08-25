using BookSpace.Infrastructure.Security;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Security;

public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ThenVerify_WithCorrectPassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("Correct-Password1!");

        Assert.True(_hasher.Verify(hash, "Correct-Password1!"));
    }

    [Fact]
    public void Verify_WithIncorrectPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("Correct-Password1!");

        Assert.False(_hasher.Verify(hash, "Wrong-Password1!"));
    }

    [Fact]
    public void Hash_ProducesDifferentOutputForTheSamePasswordEachTime()
    {
        var first = _hasher.Hash("Same-Password1!");
        var second = _hasher.Hash("Same-Password1!");

        Assert.NotEqual(first, second);
        Assert.True(_hasher.Verify(first, "Same-Password1!"));
        Assert.True(_hasher.Verify(second, "Same-Password1!"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-valid-hash")]
    [InlineData("100000.only-two-parts")]
    [InlineData("not-a-number.c2FsdA==.a2V5")]
    public void Verify_WithMalformedStoredHash_ReturnsFalseInsteadOfThrowing(string malformedHash)
    {
        Assert.False(_hasher.Verify(malformedHash, "anything"));
    }
}
