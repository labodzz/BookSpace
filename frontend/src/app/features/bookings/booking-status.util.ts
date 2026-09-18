import { BookingStatus } from './booking.models';

export function statusPillClass(status: BookingStatus): string {
  return `status-pill status-pill--${status.toLowerCase()}`;
}

export function statusLabel(status: BookingStatus): string {
  return status === 'NoShow' ? 'No-show' : status;
}
