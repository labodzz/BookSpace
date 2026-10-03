namespace BookSpace.Application.Notifications;

// Exactly what a sender needs to address an email - never a copy of the user's row. DisplayName falls
// back to the email address itself when a user somehow has no name on file (see the Infrastructure
// implementation) - never empty, since every call site treats DisplayName as safe to put straight into
// a "To:" header's display-name part.
public sealed record EmailRecipient(string Email, string DisplayName);

// Resolves the live email address for a notification's recipient - see docs/background-jobs.md
// ("Recipient lookup") for the full design. This is the one, narrow, documented exception to the
// global tenant query filter for background notification delivery (mirroring the same justification
// already used by INotificationOutboxReader/INotificationOutboxProcessor): there is no HTTP request/JWT
// claim to scope an ambient tenant context by here, so a query that only filtered by RecipientUserId
// would have nothing else stopping it from crossing a tenant boundary. Every implementation must filter
// by BOTH tenantId and recipientUserId in the same query, never recipientUserId alone.
public interface IEmailRecipientResolver
{
    // Returns null when no user exists for this exact (tenantId, recipientUserId) pair, or one exists
    // but has no usable email address - a caller must treat either case as a permanent failure (retrying
    // will not make a nonexistent user or a blank email address become deliverable).
    Task<EmailRecipient?> ResolveAsync(Guid tenantId, Guid recipientUserId, CancellationToken cancellationToken);
}
