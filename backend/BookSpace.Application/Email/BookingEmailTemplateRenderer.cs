using System.Net;
using System.Text.Json;
using BookSpace.Application.Notifications;

namespace BookSpace.Application.Email;

// Deterministic, dependency-free rendering (no Razor, no external template engine - plain string
// composition is enough for the three fixed booking lifecycle notification types) - see
// docs/background-jobs.md ("Email templates"). Every dynamic value placed into HtmlBody is HTML-encoded,
// including the recipient's own display name (the one genuinely user-controlled string involved) and the
// otherwise-safe GUID/number/date values, for defense in depth. Every date is rendered in UTC and
// explicitly labeled "UTC" - this renderer has no resource/user timezone available to it and must never
// invent one (see BookingNotificationPayload's own doc comment).
public sealed class BookingEmailTemplateRenderer : IEmailTemplateRenderer
{
    public EmailContent Render(string notificationType, string payloadJson, string recipientDisplayName)
    {
        var (subject, statusWord) = notificationType switch
        {
            BookingNotificationTypes.Confirmation => ("Your BookSpace booking is confirmed", "confirmed"),
            BookingNotificationTypes.Rejection => ("Your BookSpace booking was not approved", "not approved"),
            BookingNotificationTypes.Cancellation => ("Your BookSpace booking has been cancelled", "cancelled"),
            _ => throw new EmailTemplateException($"Unrecognized notification type '{notificationType}'."),
        };

        var payload = ParsePayload(payloadJson);
        var startUtcText = FormatUtc(payload.StartUtc);
        var endUtcText = FormatUtc(payload.EndUtc);

        var plainTextBody =
            $"Hi {recipientDisplayName},\n\n" +
            $"Your booking has been {statusWord}.\n\n" +
            $"Booking ID: {payload.BookingId}\n" +
            $"Resource ID: {payload.ResourceId}\n" +
            $"Start: {startUtcText}\n" +
            $"End: {endUtcText}\n" +
            $"Quantity: {payload.Quantity}\n\n" +
            "- BookSpace";

        var htmlBody =
            $"<p>Hi {Encode(recipientDisplayName)},</p>" +
            $"<p>Your booking has been <strong>{Encode(statusWord)}</strong>.</p>" +
            "<ul>" +
            $"<li>Booking ID: {Encode(payload.BookingId.ToString())}</li>" +
            $"<li>Resource ID: {Encode(payload.ResourceId.ToString())}</li>" +
            $"<li>Start: {Encode(startUtcText)}</li>" +
            $"<li>End: {Encode(endUtcText)}</li>" +
            $"<li>Quantity: {Encode(payload.Quantity.ToString())}</li>" +
            "</ul>" +
            "<p>- BookSpace</p>";

        return new EmailContent(subject, plainTextBody, htmlBody);
    }

    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static BookingNotificationPayload ParsePayload(string payloadJson)
    {
        BookingNotificationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BookingNotificationPayload>(payloadJson);
        }
        catch (JsonException exception)
        {
            throw new EmailTemplateException($"Notification payload is not valid JSON: {exception.Message}");
        }

        return payload ?? throw new EmailTemplateException("Notification payload deserialized to null.");
    }
}
