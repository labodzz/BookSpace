using System.Text.Json;
using BookSpace.Application.Email;
using BookSpace.Application.Notifications;
using Xunit;

namespace BookSpace.Application.Tests.Email;

public sealed class BookingEmailTemplateRendererTests
{
    private readonly BookingEmailTemplateRenderer _renderer = new();

    private static readonly BookingNotificationPayload SamplePayload = new(
        BookingId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ResourceId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        StartUtc: new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
        EndUtc: new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
        Quantity: 2);

    private static string PayloadJson() => JsonSerializer.Serialize(SamplePayload);

    [Fact]
    public void Render_ForConfirmation_ProducesAConfirmationSubjectAndBody()
    {
        var content = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");

        Assert.Equal("Your BookSpace booking is confirmed", content.Subject);
        Assert.Contains("confirmed", content.PlainTextBody);
        Assert.Contains("confirmed", content.HtmlBody);
    }

    [Fact]
    public void Render_ForRejection_ProducesARejectionSubjectAndBody()
    {
        var content = _renderer.Render(BookingNotificationTypes.Rejection, PayloadJson(), "Ana Anic");

        Assert.Equal("Your BookSpace booking was not approved", content.Subject);
        Assert.Contains("not approved", content.PlainTextBody);
        Assert.Contains("not approved", content.HtmlBody);
    }

    [Fact]
    public void Render_ForCancellation_ProducesACancellationSubjectAndBody()
    {
        var content = _renderer.Render(BookingNotificationTypes.Cancellation, PayloadJson(), "Ana Anic");

        Assert.Equal("Your BookSpace booking has been cancelled", content.Subject);
        Assert.Contains("cancelled", content.PlainTextBody);
        Assert.Contains("cancelled", content.HtmlBody);
    }

    [Fact]
    public void Render_PlainTextBody_IncludesBookingIdResourceIdQuantityAndNotHtmlMarkup()
    {
        var content = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");

        Assert.Contains(SamplePayload.BookingId.ToString(), content.PlainTextBody);
        Assert.Contains(SamplePayload.ResourceId.ToString(), content.PlainTextBody);
        Assert.Contains("2", content.PlainTextBody);
        Assert.DoesNotContain("<", content.PlainTextBody);
    }

    [Fact]
    public void Render_HtmlBody_IncludesBookingIdResourceIdAndBasicMarkup()
    {
        var content = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");

        Assert.Contains(SamplePayload.BookingId.ToString(), content.HtmlBody);
        Assert.Contains(SamplePayload.ResourceId.ToString(), content.HtmlBody);
        Assert.Contains("<p>", content.HtmlBody);
        Assert.Contains("<li>", content.HtmlBody);
    }

    [Fact]
    public void Render_TimesAreLabeledUtc_NeverInventingAnotherTimezone()
    {
        var content = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");

        Assert.Contains("2026-03-01 09:00 UTC", content.PlainTextBody);
        Assert.Contains("2026-03-01 10:00 UTC", content.PlainTextBody);
        Assert.Contains("2026-03-01 09:00 UTC", content.HtmlBody);
    }

    [Fact]
    public void Render_HtmlEscapesTheRecipientDisplayName()
    {
        const string maliciousName = "<script>alert('x')</script>";

        var content = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), maliciousName);

        Assert.DoesNotContain("<script>", content.HtmlBody);
        Assert.Contains("&lt;script&gt;", content.HtmlBody);
        // The plain-text body is never HTML-rendered, so the raw name is correct and expected there.
        Assert.Contains(maliciousName, content.PlainTextBody);
    }

    [Fact]
    public void Render_IsDeterministic_SameInputsProduceIdenticalOutputOnEveryCall()
    {
        var first = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");
        var second = _renderer.Render(BookingNotificationTypes.Confirmation, PayloadJson(), "Ana Anic");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Render_WithMalformedPayloadJson_ThrowsEmailTemplateException()
    {
        Assert.Throws<EmailTemplateException>(() =>
            _renderer.Render(BookingNotificationTypes.Confirmation, "{not-valid-json", "Ana Anic"));
    }

    [Fact]
    public void Render_WithPayloadMissingRequiredFields_ThrowsEmailTemplateException()
    {
        Assert.Throws<EmailTemplateException>(() =>
            _renderer.Render(BookingNotificationTypes.Confirmation, "null", "Ana Anic"));
    }

    [Fact]
    public void Render_WithAnUnrecognizedNotificationType_ThrowsEmailTemplateException()
    {
        Assert.Throws<EmailTemplateException>(() =>
            _renderer.Render("Booking.SomethingElse", PayloadJson(), "Ana Anic"));
    }
}
