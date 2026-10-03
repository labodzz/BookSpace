using BookSpace.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// The one, narrow, documented exception to the global tenant query filter for background email delivery
// - see docs/tenant-isolation.md and docs/background-jobs.md ("Recipient lookup"). Background processing
// has no HTTP request/JWT claim to scope an ambient tenant by (the same justification already used by
// NotificationOutboxReader/NotificationOutboxProcessor), so this query filters by BOTH tenantId and
// recipientUserId explicitly in the one Where clause - never recipientUserId alone - so a cross-tenant
// (tenantId, recipientUserId) combination can never resolve to a row that exists under a different
// tenant: a user's TenantId and Id are fixed together on the same row, so filtering on both in one
// predicate is what makes crossing a tenant boundary structurally impossible here, not merely unlikely.
internal sealed class EmailRecipientResolver(BookSpaceDbContext dbContext, ILogger<EmailRecipientResolver> logger) : IEmailRecipientResolver
{
    public async Task<EmailRecipient?> ResolveAsync(Guid tenantId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        var row = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(user => user.TenantId == tenantId && user.Id == recipientUserId)
            .Select(user => new { user.Email, user.FirstName, user.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            logger.LogInformation(
                "Notification recipient lookup found no user. TenantId={TenantId} RecipientUserId={RecipientUserId}", tenantId, recipientUserId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(row.Email))
        {
            logger.LogInformation(
                "Notification recipient lookup found a user with no usable email address. TenantId={TenantId} RecipientUserId={RecipientUserId}",
                tenantId, recipientUserId);
            return null;
        }

        var displayName = $"{row.FirstName} {row.LastName}".Trim();
        return new EmailRecipient(row.Email, string.IsNullOrWhiteSpace(displayName) ? row.Email : displayName);
    }
}
