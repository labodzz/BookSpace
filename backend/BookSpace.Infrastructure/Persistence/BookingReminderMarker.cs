using BookSpace.Application.Notifications;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// See IBookingReminderMarker for the contract and docs/background-jobs.md ("Reminder job - the
// ReminderQueuedAtUtc marker") for the full design. Attaches a stub Booking (Id only) and marks exactly one
// property Modified, rather than loading the real tracked entity first - this needs no SELECT round trip
// and, critically, never risks EF Core's identity map silently reusing an already-tracked instance of the
// same booking elsewhere on this DbContext (the same identity-map trap docs/recurring-bookings-and-
// approvals.md documents for a different handler): a stub Attach only works at all if nothing else on this
// DbContext has already tracked that same Booking by the time this runs, which holds here because
// ReminderProducer's candidates come from IBookingReminderReader's own AsNoTracking() projection, never a
// tracked entity load.
internal sealed class BookingReminderMarker(BookSpaceDbContext dbContext) : IBookingReminderMarker
{
    public void StageReminderQueued(Guid bookingId, DateTimeOffset queuedAtUtc)
    {
        var stub = new Booking { Id = bookingId };
        dbContext.Attach(stub);
        stub.ReminderQueuedAtUtc = queuedAtUtc;
        dbContext.Entry(stub).Property(booking => booking.ReminderQueuedAtUtc).IsModified = true;
    }
}
