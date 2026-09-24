import { BookingStatus } from './booking.models';

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
