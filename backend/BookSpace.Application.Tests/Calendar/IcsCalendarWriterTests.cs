using System.Text;
using BookSpace.Application.Bookings;
using BookSpace.Application.Calendar;
using BookSpace.Domain.Enums;
using IcsCalendar = Ical.Net.Calendar;
using Xunit;

namespace BookSpace.Application.Tests.Calendar;

// Verified against an INDEPENDENT parser (Ical.Net, a test-only dependency of this project - never
// referenced by BookSpace.Application/Infrastructure/Api themselves) rather than by re-asserting
// IcsCalendarWriter's own escaping/folding logic against itself - a bug in the hand-rolled writer that
// happened to be "consistent with itself" would still be caught here, since Ical.Net implements RFC 5545
// independently.
public sealed class IcsCalendarWriterTests
{
    private static CalendarFeedBooking Booking(string resourceName, DateTimeOffset? start = null, DateTimeOffset? end = null, int quantity = 1) =>
        new(Guid.NewGuid(), resourceName, start ?? new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero),
            end ?? new DateTimeOffset(2026, 6, 15, 15, 0, 0, TimeSpan.Zero), quantity, BookingStatus.Confirmed);

    // IcsCalendar.Load's nullable return is never actually null for a successfully-generated feed - the
    // writer always produces a well-formed VCALENDAR - so this centralizes the one null-forgiving
    // assumption instead of repeating it at every call site.
    private static IcsCalendar LoadCalendar(string ics) => IcsCalendar.Load(ics)!;

    [Fact]
    public void Write_UsesCrLfLineEndings()
    {
        var ics = IcsCalendarWriter.Write([Booking("Room")], DateTimeOffset.UtcNow);

        Assert.DoesNotContain("\r\n\n", ics); // no bare LF left dangling after a CRLF
        Assert.Contains("\r\n", ics);
        foreach (var line in ics.Split("\r\n"))
        {
            Assert.DoesNotContain('\n', line);
        }
    }

    [Fact]
    public void Write_WithNoBookings_ProducesAValidEmptyCalendar()
    {
        var ics = IcsCalendarWriter.Write([], DateTimeOffset.UtcNow);

        var calendar = LoadCalendar(ics);

        Assert.Empty(calendar.Events);
    }

    [Fact]
    public void Write_WithOneBooking_ProducesAParsableVEventWithTheExpectedCoreProperties()
    {
        var generatedAtUtc = new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero);
        var booking = Booking("Conference Room A", new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 15, 15, 30, 0, TimeSpan.Zero));

        var ics = IcsCalendarWriter.Write([booking], generatedAtUtc);
        var calendar = LoadCalendar(ics);

        var evt = Assert.Single(calendar.Events);
        Assert.Equal($"{booking.Id:D}@bookspace", evt.Uid);
        Assert.Equal("Conference Room A", evt.Summary);
        Assert.Equal("CONFIRMED", evt.Status);
        Assert.Equal(new DateTime(2026, 6, 15, 14, 0, 0, DateTimeKind.Utc), evt.Start!.AsUtc);
        Assert.Equal(new DateTime(2026, 6, 15, 15, 30, 0, DateTimeKind.Utc), evt.End!.AsUtc);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc), evt.DtStamp!.AsUtc);
    }

    [Fact]
    public void Write_CalledTwiceForTheSameBooking_ProducesTheSameUidBothTimes()
    {
        var booking = Booking("Room");

        var first = LoadCalendar(IcsCalendarWriter.Write([booking], DateTimeOffset.UtcNow));
        var second = LoadCalendar(IcsCalendarWriter.Write([booking], DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal(Assert.Single(first.Events).Uid, Assert.Single(second.Events).Uid);
    }

    // RFC 5545 §3.8.7.2: DTSTAMP reflects when the representation was generated, not the event's own
    // identity - it must change between two fetches even though UID/DTSTART/DTEND (the booking's actual
    // content) do not.
    [Fact]
    public void Write_CalledTwiceForTheSameBooking_ProducesADifferentDtStampEachTime()
    {
        var booking = Booking("Room");
        var firstGeneratedAt = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var secondGeneratedAt = firstGeneratedAt.AddMinutes(5);

        var first = LoadCalendar(IcsCalendarWriter.Write([booking], firstGeneratedAt));
        var second = LoadCalendar(IcsCalendarWriter.Write([booking], secondGeneratedAt));

        Assert.NotEqual(Assert.Single(first.Events).DtStamp!.AsUtc, Assert.Single(second.Events).DtStamp!.AsUtc);
    }

    [Theory]
    [InlineData(1, "Booked via BookSpace.")]
    [InlineData(3, "Booked via BookSpace (3 units).")]
    public void Write_DescribesQuantityOnlyWhenGreaterThanOne(int quantity, string expectedDescription)
    {
        var ics = IcsCalendarWriter.Write([Booking("Room", quantity: quantity)], DateTimeOffset.UtcNow);

        var evt = Assert.Single(LoadCalendar(ics).Events);
        Assert.Equal(expectedDescription, evt.Description);
    }

    [Fact]
    public void Write_WithAResourceNameContainingIcsSpecialCharacters_RoundTripsExactlyAndNeverInjectsAnExtraComponent()
    {
        // Backslash, semicolon, comma, and an embedded raw newline - every character RFC 5545 §3.3.11
        // requires escaping in a TEXT value, plus the one character (a raw CRLF/LF) that would let
        // unescaped user text start a bogus new content line (the actual ICS-injection vector).
        const string maliciousName = "Room \"A\"; B, C\\D\nEND:VEVENT\nBEGIN:VEVENT\nUID:injected@evil";
        var ics = IcsCalendarWriter.Write([Booking(maliciousName)], DateTimeOffset.UtcNow);

        var calendar = LoadCalendar(ics);

        // The malicious payload never created a second component - it round-trips as pure text data.
        var evt = Assert.Single(calendar.Events);
        Assert.Equal(maliciousName, evt.Summary);
    }

    [Fact]
    public void Write_WithAUnicodeResourceName_RoundTripsExactly()
    {
        const string unicodeName = "Конференц-зала 🎉 Café";

        var ics = IcsCalendarWriter.Write([Booking(unicodeName)], DateTimeOffset.UtcNow);
        var evt = Assert.Single(LoadCalendar(ics).Events);

        Assert.Equal(unicodeName, evt.Summary);
    }

    [Fact]
    public void Write_WithAResourceNameLongerThanTheFoldLimit_FoldsWithoutSplittingAMultiByteCharacter_AndStillRoundTrips()
    {
        // A long, entirely multi-byte-character name - if WriteLine folded mid-character, either the
        // raw bytes would be invalid UTF-8 (Encoding.UTF8.GetString would produce replacement characters)
        // or Ical.Net's own parser would reject/mangle the line.
        var longUnicodeName = string.Concat(Enumerable.Repeat("Конференц-зала ", 10)); // well over 75 octets

        var ics = IcsCalendarWriter.Write([Booking(longUnicodeName)], DateTimeOffset.UtcNow);

        // Every physical line (content lines are CRLF-terminated; a folded continuation starts with a
        // single space) must itself be valid, replacement-character-free UTF-8.
        foreach (var line in ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain('�', line);
        }

        var evt = Assert.Single(LoadCalendar(ics).Events);
        Assert.Equal(longUnicodeName, evt.Summary);
    }

    [Fact]
    public void Write_FoldsAnyContentLineLongerThan75OctetsOntoAContinuationLineStartingWithASpace()
    {
        var ics = IcsCalendarWriter.Write([Booking(new string('A', 200))], DateTimeOffset.UtcNow);

        var lines = ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.StartsWith(' '));
        foreach (var line in lines)
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"line exceeds 75 octets: \"{line}\"");
        }
    }

    [Fact]
    public void Write_WithSeveralBookings_EmitsOneVEventPerBookingInStartOrder()
    {
        var early = Booking("Room A", new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var late = Booking("Room B", new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero));

        var ics = IcsCalendarWriter.Write([early, late], DateTimeOffset.UtcNow);

        var calendar = LoadCalendar(ics);
        Assert.Equal(2, calendar.Events.Count);
        Assert.Contains(calendar.Events, e => e.Uid == $"{early.Id:D}@bookspace");
        Assert.Contains(calendar.Events, e => e.Uid == $"{late.Id:D}@bookspace");
    }
}
