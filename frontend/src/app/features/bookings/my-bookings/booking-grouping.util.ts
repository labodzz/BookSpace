import { OwnBooking } from '../booking.models';

export type BookingListEntry =
  | { readonly kind: 'single'; readonly booking: OwnBooking }
  | { readonly kind: 'series'; readonly seriesId: string; readonly resourceId: string; readonly occurrences: OwnBooking[] };

// Collapses consecutive same-series occurrences into a single group entry, so a 20-occurrence weekly
// series shows as one row in My Bookings instead of twenty. GetOwnBookingsAsync orders results by
// CreatedAtUtc (falling back to Id as a tiebreaker), and every occurrence of one series is created
// together by the same CreateRecurringSeriesCommandRequest, so a series' rows always arrive contiguously
// on any one page - only ADJACENT same-seriesId bookings are merged here, and the list is never reordered.
export function groupBookingsForDisplay(bookings: readonly OwnBooking[]): BookingListEntry[] {
  const entries: BookingListEntry[] = [];
  for (const booking of bookings) {
    const last = entries[entries.length - 1];
    if (booking.seriesId && last?.kind === 'series' && last.seriesId === booking.seriesId) {
      last.occurrences.push(booking);
      continue;
    }
    entries.push(
      booking.seriesId
        ? { kind: 'series', seriesId: booking.seriesId, resourceId: booking.resourceId, occurrences: [booking] }
        : { kind: 'single', booking },
    );
  }
  return entries;
}
