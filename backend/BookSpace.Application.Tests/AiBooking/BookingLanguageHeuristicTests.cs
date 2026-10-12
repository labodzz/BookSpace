using BookSpace.Application.AiBooking;
using Xunit;

namespace BookSpace.Application.Tests.AiBooking;

public sealed class BookingLanguageHeuristicTests
{
    [Theory]
    [InlineData("Treba mi laboratorija za 3 osobe sljedeći utorak od 14 do 16.")]
    [InlineData("Treba mi laboratorija za 3 osobe sljedeci utorak od 14 do 16.")] // same request, no diacritics
    [InlineData("Trebam salu sutra popodne.")]
    public void Detect_WithBosnianText_ReturnsBs(string text) => Assert.Equal("bs", BookingLanguageHeuristic.Detect(text));

    [Theory]
    [InlineData("Book a lab for 3 people next Tuesday from 2 to 4 PM.")]
    [InlineData("I need a meeting room tomorrow afternoon.")]
    public void Detect_WithEnglishText_ReturnsEn(string text) => Assert.Equal("en", BookingLanguageHeuristic.Detect(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Detect_WithEmptyOrWhitespaceText_DefaultsToEn(string? text) => Assert.Equal("en", BookingLanguageHeuristic.Detect(text!));
}
