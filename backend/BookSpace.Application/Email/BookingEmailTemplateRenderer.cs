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
        if (notificationType == BookingNotificationTypes.ApprovalExpired)
        {
            return RenderApprovalExpired(payloadJson, recipientDisplayName);
        }

        if (notificationType == TenantNotificationTypes.AdminInvitation)
        {
            return RenderTenantAdminInvitation(payloadJson, recipientDisplayName);
        }

        var (subject, introSentence) = notificationType switch
        {
            BookingNotificationTypes.Confirmation => ("Your BookSpace booking is confirmed", "Your booking has been confirmed."),
            BookingNotificationTypes.Rejection => ("Your BookSpace booking was not approved", "Your booking has been not approved."),
            BookingNotificationTypes.Cancellation => ("Your BookSpace booking has been cancelled", "Your booking has been cancelled."),
            BookingNotificationTypes.Reminder => ("Reminder: your BookSpace booking starts soon", "This is a reminder that your booking is starting soon."),
            BookingNotificationTypes.NoShow => (
                "Your BookSpace booking was marked as a no-show",
                "No check-in was recorded before the grace period ended, so this booking was marked as a no-show and its reserved capacity has been released."),
            _ => throw new EmailTemplateException($"Unrecognized notification type '{notificationType}'."),
        };

        var payload = ParsePayload(payloadJson);
        var startUtcText = FormatUtc(payload.StartUtc);
        var endUtcText = FormatUtc(payload.EndUtc);

        var plainTextBody =
            $"Hi {recipientDisplayName},\n\n" +
            $"{introSentence}\n\n" +
            $"Booking ID: {payload.BookingId}\n" +
            $"Resource ID: {payload.ResourceId}\n" +
            $"Start: {startUtcText}\n" +
            $"End: {endUtcText}\n" +
            $"Quantity: {payload.Quantity}\n\n" +
            "- BookSpace";

        var htmlBody =
            $"<p>Hi {Encode(recipientDisplayName)},</p>" +
            $"<p>{Encode(introSentence)}</p>" +
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

    // A separate render path, not another branch of the switch above: Booking.ApprovalExpired's payload
    // (ApprovalExpiredNotificationPayload) carries one extra field (ApprovalRequestId) no other booking
    // lifecycle notification needs - see BookingNotificationFactory's own doc comment for why that is its
    // own record type rather than squeezed into BookingNotificationPayload.
    private static EmailContent RenderApprovalExpired(string payloadJson, string recipientDisplayName)
    {
        var payload = ParseApprovalExpiredPayload(payloadJson);
        var startUtcText = FormatUtc(payload.StartUtc);
        var endUtcText = FormatUtc(payload.EndUtc);
        const string subject = "Your BookSpace booking approval request expired";
        const string introSentence =
            "Nobody approved or rejected your booking request before the approval window closed, so it has automatically expired.";

        var plainTextBody =
            $"Hi {recipientDisplayName},\n\n" +
            $"{introSentence}\n\n" +
            $"Booking ID: {payload.BookingId}\n" +
            $"Approval Request ID: {payload.ApprovalRequestId}\n" +
            $"Resource ID: {payload.ResourceId}\n" +
            $"Start: {startUtcText}\n" +
            $"End: {endUtcText}\n" +
            $"Quantity: {payload.Quantity}\n\n" +
            "- BookSpace";

        var htmlBody =
            $"<p>Hi {Encode(recipientDisplayName)},</p>" +
            $"<p>{Encode(introSentence)}</p>" +
            "<ul>" +
            $"<li>Booking ID: {Encode(payload.BookingId.ToString())}</li>" +
            $"<li>Approval Request ID: {Encode(payload.ApprovalRequestId.ToString())}</li>" +
            $"<li>Resource ID: {Encode(payload.ResourceId.ToString())}</li>" +
            $"<li>Start: {Encode(startUtcText)}</li>" +
            $"<li>End: {Encode(endUtcText)}</li>" +
            $"<li>Quantity: {Encode(payload.Quantity.ToString())}</li>" +
            "</ul>" +
            "<p>- BookSpace</p>";

        return new EmailContent(subject, plainTextBody, htmlBody);
    }

    // A third, separate render path, not a branch of the main booking-lifecycle switch above:
    // TenantAdminInvitationNotificationPayload shares no fields at all with BookingNotificationPayload
    // (no BookingId/ResourceId - this notification is about a brand-new tenant and its first
    // administrator, not an existing booking). The invitation link itself (InvitationUrl) is the one
    // genuinely sensitive value this renderer ever handles - it is placed into the email body as-is
    // (HTML-encoded like every other dynamic value, never logged - see docs/tenant-onboarding.md), the
    // email's entire purpose being to deliver exactly that link to the invited administrator.
    private static EmailContent RenderTenantAdminInvitation(string payloadJson, string recipientDisplayName)
    {
        var payload = ParseTenantAdminInvitationPayload(payloadJson);
        var expiresAtText = FormatUtc(payload.ExpiresAtUtc);
        var subject = $"You've been invited to administer {payload.TenantName} on BookSpace";

        var plainTextBody =
            $"Hi {recipientDisplayName},\n\n" +
            $"You've been invited to be the administrator for \"{payload.TenantName}\" on BookSpace.\n\n" +
            $"Set your password to accept the invitation and sign in:\n{payload.InvitationUrl}\n\n" +
            $"This link expires {expiresAtText} and can only be used once.\n\n" +
            "If you weren't expecting this invitation, you can safely ignore this email.\n\n" +
            "- BookSpace";

        var htmlBody =
            $"<p>Hi {Encode(recipientDisplayName)},</p>" +
            $"<p>You've been invited to be the administrator for \"{Encode(payload.TenantName)}\" on BookSpace.</p>" +
            $"<p><a href=\"{Encode(payload.InvitationUrl)}\">Set your password to accept the invitation and sign in</a></p>" +
            $"<p>This link expires {Encode(expiresAtText)} and can only be used once.</p>" +
            "<p>If you weren't expecting this invitation, you can safely ignore this email.</p>" +
            "<p>- BookSpace</p>";

        return new EmailContent(subject, plainTextBody, htmlBody);
    }

    private static TenantAdminInvitationNotificationPayload ParseTenantAdminInvitationPayload(string payloadJson)
    {
        TenantAdminInvitationNotificationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TenantAdminInvitationNotificationPayload>(payloadJson);
        }
        catch (JsonException exception)
        {
            throw new EmailTemplateException($"Notification payload is not valid JSON: {exception.Message}");
        }

        return payload ?? throw new EmailTemplateException("Notification payload deserialized to null.");
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

    private static ApprovalExpiredNotificationPayload ParseApprovalExpiredPayload(string payloadJson)
    {
        ApprovalExpiredNotificationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ApprovalExpiredNotificationPayload>(payloadJson);
        }
        catch (JsonException exception)
        {
            throw new EmailTemplateException($"Notification payload is not valid JSON: {exception.Message}");
        }

        return payload ?? throw new EmailTemplateException("Notification payload deserialized to null.");
    }
}
