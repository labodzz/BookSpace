using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// See IBookingReminderEligibilityChecker for the contract and docs/background-jobs.md ("Cancellation race
// for reminders") for the full design. Mirrors EmailRecipientResolver's shape exactly: filters by BOTH
// tenantId and bookingId in the same Where clause (never bookingId alone), so a cross-tenant combination
// structurally can never resolve to a row that exists under a different tenant.
internal sealed class BookingReminderEligibilityChecker(BookSpaceDbContext dbContext, ILogger<BookingReminderEligibilityChecker> logger)
    : IBookingReminderEligibilityChecker
{
    public async Task<bool> IsStillEligibleAsync(Guid tenantId, Guid bookingId, CancellationToken cancellationToken)
    {
        var row = await dbContext.Bookings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(booking => booking.TenantId == tenantId && booking.Id == bookingId)
            .Select(booking => new { booking.Status, booking.StartUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            logger.LogInformation(
                "Booking reminder eligibility check found no booking. TenantId={TenantId} BookingId={BookingId}", tenantId, bookingId);
            return false;
        }

        if (row.Status != BookingStatus.Confirmed)
        {
            logger.LogInformation(
                "Booking reminder eligibility check found a booking that is no longer Confirmed. TenantId={TenantId} BookingId={BookingId} Status={Status}",
                tenantId, bookingId, row.Status);
            return false;
        }

        // DateTimeOffset.UtcNow inline, not captured first - same server-side-clock reasoning as
        // BookingReminderReader/NotificationOutboxReader: this comparison happens in application code
        // (there is exactly one row to check, not a set-based query worth pushing server-side), but using
        // the same clock source consistently throughout this feature avoids a reminder being suppressed or
        // allowed based on a different notion of "now" than the one that decided it was due in the first
        // place.
        if (row.StartUtc <= DateTimeOffset.UtcNow)
        {
            logger.LogInformation(
                "Booking reminder eligibility check found a booking that has already started. TenantId={TenantId} BookingId={BookingId}",
                tenantId, bookingId);
            return false;
        }

        return true;
    }
}
