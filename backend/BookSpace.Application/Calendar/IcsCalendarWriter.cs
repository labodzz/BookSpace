using System.Globalization;
using System.Text;
using BookSpace.Application.Bookings;

namespace BookSpace.Application.Calendar;

// Hand-rolled, deliberately minimal RFC 5545 (https://www.rfc-editor.org/rfc/rfc5545) writer - this feed
// only ever needs a fixed, small property set (no recurrence rules, no timezones beyond UTC, no
// attendees/organizers/alarms), so a full iCalendar library would bring far more surface area than this
// ever exercises. See docs/calendar-subscription-feed.md for the overall design. Verified in
// IcsCalendarWriterTests against an INDEPENDENT parser (Ical.Net, a test-only dependency), not merely by
// re-asserting this class's own escaping/folding logic against itself.
public static class IcsCalendarWriter
{
    // RFC 5545 §3.1: content lines SHOULD be folded at 75 OCTETS (not characters) by inserting a CRLF
    // followed by a single space before continuing. A UTF-8 multi-byte character must never be split
    // across that boundary - WriteLine below only ever cuts on a boundary that is itself a valid UTF-8
    // character start.
    private const int FoldLimitOctets = 75;

    public static string Write(IReadOnlyList<CalendarFeedBooking> bookings, DateTimeOffset generatedAtUtc)
    {
        var builder = new StringBuilder();
        WriteLine(builder, "BEGIN:VCALENDAR");
        WriteLine(builder, "VERSION:2.0");
        WriteLine(builder, "PRODID:-//BookSpace//Calendar Subscription Feed//EN");
        WriteLine(builder, "CALSCALE:GREGORIAN");
        // Widely-supported (Google Calendar, Apple Calendar), non-standard calendar-name hint - purely
        // cosmetic, never relied on for correctness.
        WriteLine(builder, "X-WR-CALNAME:" + EscapeText("BookSpace Bookings"));

        foreach (var booking in bookings)
        {
            WriteLine(builder, "BEGIN:VEVENT");
            // UID is Booking.Id, not a freshly minted value - stable across every refetch of this same
            // booking, which is what lets a calendar client recognize "this is the same event, possibly
            // updated" instead of creating a duplicate on every poll. See
            // docs/calendar-subscription-feed.md ("Stable UID").
            WriteLine(builder, $"UID:{booking.Id:D}@bookspace");
            // DTSTAMP is "when this representation of the object was generated" (RFC 5545 §3.8.7.2), not
            // part of the event's own identity - deliberately recomputed on every single fetch, unlike
            // UID/DTSTART/DTEND, which only change if the underlying booking itself does.
            WriteLine(builder, $"DTSTAMP:{FormatUtc(generatedAtUtc)}");
            WriteLine(builder, $"DTSTART:{FormatUtc(booking.StartUtc)}");
            WriteLine(builder, $"DTEND:{FormatUtc(booking.EndUtc)}");
            WriteLine(builder, "SUMMARY:" + EscapeText(booking.ResourceName));
            WriteLine(builder, "DESCRIPTION:" + EscapeText(BuildDescription(booking)));
            WriteLine(builder, "STATUS:CONFIRMED");
            WriteLine(builder, "END:VEVENT");
        }

        WriteLine(builder, "END:VCALENDAR");
        return builder.ToString();
    }

    private static string BuildDescription(CalendarFeedBooking booking) =>
        booking.Quantity == 1
            ? "Booked via BookSpace."
            : $"Booked via BookSpace ({booking.Quantity.ToString(CultureInfo.InvariantCulture)} units).";

    // Always UTC with a trailing Z (RFC 5545 §3.3.5 "form #2: date with UTC time") - the one unambiguous
    // instant representation every calendar client converts to its own displayed timezone itself.
    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // RFC 5545 §3.3.11 (TEXT): backslash, semicolon, and comma are escaped with a leading backslash; a
    // literal newline becomes the two-character sequence "\n". Escaping every raw CR/LF this way - and
    // never emitting one unescaped from user/DB-controlled text (a Resource.Name here) - is what makes ICS
    // injection impossible: the only way a content line in this output can ever end is a real CRLF
    // WriteLine itself writes, never one hidden inside a resource name. Backslash is escaped FIRST, before
    // the others, so the backslashes this method itself inserts below are never themselves re-escaped.
    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");

    // Writes one logical property as one or more physical lines, each terminated CRLF and folded per RFC
    // 5545 §3.1 - never folds in the middle of a UTF-8 multi-byte sequence.
    private static void WriteLine(StringBuilder builder, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var offset = 0;
        var first = true;
        while (offset < bytes.Length || first)
        {
            if (!first)
            {
                builder.Append("\r\n ");
            }

            // The continuation line always starts with a single space (one octet), so the first segment
            // may use the full fold limit but every later segment has one fewer octet of budget for content.
            var budget = first ? FoldLimitOctets : FoldLimitOctets - 1;
            var length = Math.Min(budget, bytes.Length - offset);
            // Never split a multi-byte UTF-8 sequence: a continuation byte has its top two bits set to 10
            // (0x80-0xBF) - back off until the cut point is NOT in the middle of one.
            while (length > 0 && offset + length < bytes.Length && (bytes[offset + length] & 0xC0) == 0x80)
            {
                length--;
            }

            builder.Append(Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
            first = false;
        }

        builder.Append("\r\n");
    }
}
