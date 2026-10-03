namespace BookSpace.Application.Email;

// Rendered content for one outgoing notification email - exactly what a sender needs to build a
// MimeMessage, nothing provider-specific (no MIME structure, no headers).
public sealed record EmailContent(string Subject, string PlainTextBody, string HtmlBody);

// Thrown when a notification cannot be rendered at all - a malformed PayloadJson or an unrecognized
// NotificationType. A caller (the production INotificationSender) must treat this as a permanent
// failure: retrying will not make a malformed payload or an unknown type become renderable.
public sealed class EmailTemplateException(string message) : Exception(message);

// Deterministic rendering of one notification's email content - the same (notificationType, payloadJson)
// always produces the same Subject/PlainTextBody/HtmlBody, aside from the recipient's own display name
// (passed in separately - this renderer has no part in resolving it). See docs/background-jobs.md
// ("Email templates") for the full design and why no template engine is used.
public interface IEmailTemplateRenderer
{
    EmailContent Render(string notificationType, string payloadJson, string recipientDisplayName);
}
