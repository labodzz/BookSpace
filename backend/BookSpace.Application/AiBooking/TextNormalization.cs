using System.Globalization;
using System.Text;

namespace BookSpace.Application.AiBooking;

// Resource/resource-type name matching must work whether the caller (a human, or the model relaying what
// the human typed) writes Bosnian WITH diacritics ("učionica") or WITHOUT them ("ucionica") - see
// docs/ai-assisted-booking.md ("Bosnian without diacritics"). .NET has no built-in "fold č/ć/š/đ/ž to
// their plain Latin letter" - Unicode's own NFD decomposition strips the general Latin diacritical marks
// (grave/acute/circumflex/diaeresis etc.) via UnicodeCategory.NonSpacingMark, but it does NOT decompose
// đ/Đ at all (it has no accent-letter decomposition; it's its own distinct code point) - so that single
// case is normalized by an explicit character map after the general NFD pass.
internal static class TextNormalization
{
    public static string FoldToPlainLatinLowercase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(ch switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => ch,
            });
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    // Case/diacritic-insensitive "does haystack contain needle" - the one comparison every resource-type/
    // resource-name match in ProposeBookingCommandHandler uses, so "Učionica"/"ucionica"/"UCIONICA" are
    // all treated as the same request.
    public static bool FoldedContains(string haystack, string needle) =>
        FoldToPlainLatinLowercase(haystack).Contains(FoldToPlainLatinLowercase(needle), StringComparison.Ordinal);

    public static bool FoldedEquals(string first, string second) =>
        string.Equals(FoldToPlainLatinLowercase(first), FoldToPlainLatinLowercase(second), StringComparison.Ordinal);
}
