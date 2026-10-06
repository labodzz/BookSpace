import { OwnBooking, BookingStatus } from './booking.models';

export function statusPillClass(status: BookingStatus): string {
  return `status-pill status-pill--${status.toLowerCase()}`;
}

export function statusLabel(status: BookingStatus): string {
  return status === 'NoShow' ? 'No-show' : status;
}

const CANCELLABLE_STATUSES: readonly BookingStatus[] = ['Pending', 'Confirmed'];

export function canCancelBooking(status: BookingStatus): boolean {
  return CANCELLABLE_STATUSES.includes(status);
}

// Mirrors CheckInEligibilityChecker's window definition (backend/BookSpace.Application/Bookings/
// CheckInEligibilityChecker.cs): both boundaries inclusive. Takes the whole booking (not just status) since
// the window itself is server-computed per booking (checkInWindowOpensAtUtc/ClosesAtUtc, from the caller's
// own tenant settings) - this function only decides whether the Check-in BUTTON should be ENABLED right
// now; whether it's shown at all is a separate, coarser check (Confirmed and not yet checked in - see
// booking-row.html), since a closed/not-yet-open window still shows the button, just disabled.
export function canCheckInBooking(booking: OwnBooking, nowUtc: Date = new Date()): boolean {
  if (booking.status !== 'Confirmed' || booking.checkedInAtUtc !== null) {
    return false;
  }
  const now = nowUtc.getTime();
  return now >= new Date(booking.checkInWindowOpensAtUtc).getTime() && now <= new Date(booking.checkInWindowClosesAtUtc).getTime();
}
