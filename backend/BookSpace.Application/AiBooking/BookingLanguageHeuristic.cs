namespace BookSpace.Application.AiBooking;

// Only used for the two outcomes where no LlmBookingIntent was ever obtained at all - Disabled (the
// feature never calls the model) and ProviderUnavailable (the call failed/timed out) - see
// ProposeBookingCommandHandler. Every other outcome uses the model's OWN declared Language field instead
// (validated against the fixed "bs"/"en" enum, never trusted beyond that) since the model actually read
// the full request and is far better positioned to judge its language than this. A small, fixed set of
// distinctive Bosnian signals (diacritic letters, and common connector/booking words written both with
// and without diacritics) is deliberately conservative: it only ever claims "bs" when fairly confident,
// defaulting to "en" otherwise - this only decides the wording of a single, short, generic error message,
// never anything the user could mistake for the assistant having understood their actual request.
internal static class BookingLanguageHeuristic
{
    private static readonly char[] BosnianDiacriticLetters = ['č', 'ć', 'š', 'đ', 'ž'];

    private static readonly string[] BosnianWords =
    [
        "treba", "trebam", "sljedeci", "sljedeći", "sutra", "danas", "prekosutra", "osoba", "osobe",
        "rezervi", "zakazi", "zakaži", "soba", "ucionica", "učionica", "laboratorija", "popodne", "ujutro",
        "navece", "navečer", "ponedjeljak", "utorak", "srijeda", "cetvrtak", "četvrtak", "petak", "subota", "nedjelja",
    ];

    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "en";
        }

        if (text.IndexOfAny(BosnianDiacriticLetters) >= 0)
        {
            return "bs";
        }

        var folded = TextNormalization.FoldToPlainLatinLowercase(text);
        return BosnianWords.Any(word => folded.Contains(TextNormalization.FoldToPlainLatinLowercase(word), StringComparison.Ordinal))
            ? "bs"
            : "en";
    }
}
