using BookSpace.Domain.Enums;

namespace BookSpace.Application.Bookings;

// Projection for IBookingRepository.GetForCalendarFeedAsync - a plain shape, not the Booking entity
// itself, so the query can project the resource name directly via a server-side join instead of a
// separate per-feed resource lookup. Unlike GetOwnBookingsQueryHandler's own page-of-resources batch load
// (IResourceRepository.GetByIdsAsync), that path isn't available here: it also relies on the ambient
// tenant query filter, which the anonymous calendar-feed request has no ICurrentUserContext.TenantId to
// drive - see ICalendarSubscriptionRepository.FindActiveByTokenHashAsync for the same reasoning.
public sealed record CalendarFeedBooking(Guid Id, string ResourceName, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Quantity, BookingStatus Status);
