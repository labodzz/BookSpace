using BookSpace.Application.AiBooking;
using Xunit;

namespace BookSpace.Application.Tests.AiBooking;

public sealed class TextNormalizationTests
{
    [Theory]
    [InlineData("učionica", "ucionica")]
    [InlineData("Učionica", "Ucionica")]
    [InlineData("Đačka soba", "Dacka soba")]
    [InlineData("čćšđž", "ccsdz")]
    [InlineData("plain text", "plain text")]
    public void FoldToPlainLatinLowercase_StripsDiacriticsAndLowercases(string input, string expectedBeforeLowercasing)
    {
        Assert.Equal(expectedBeforeLowercasing.ToLowerInvariant(), TextNormalization.FoldToPlainLatinLowercase(input));
    }

    [Theory]
    [InlineData("Učionica 101", "ucionica", true)]
    [InlineData("Učionica 101", "UCIONICA", true)]
    [InlineData("Ucionica 101", "učionica", true)] // the needle itself may also lack diacritics
    [InlineData("Meeting Room", "lab", false)]
    public void FoldedContains_IsCaseAndDiacriticInsensitive(string haystack, string needle, bool expected)
    {
        Assert.Equal(expected, TextNormalization.FoldedContains(haystack, needle));
    }
}
