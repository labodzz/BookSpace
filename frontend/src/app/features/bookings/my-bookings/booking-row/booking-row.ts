import { Component, input, output } from '@angular/core';
import { canCancelBooking, statusLabel, statusPillClass } from '../../booking-status.util';
import { OwnBooking } from '../../booking.models';
import { toLocalDateTime } from '../../local-time.util';

// One booking's row, shared by My Bookings' plain (one-off) rows and by each occurrence listed inside an
// expanded recurring-series group - `compact` hides the resource name and the "recurring" badge for the
// latter, since the group's own header already shows both and repeating them on every occurrence row
// would just be noise.
@Component({
  selector: 'app-booking-row',
  imports: [],
  templateUrl: './booking-row.html',
  styleUrl: './booking-row.scss',
})
export class BookingRowComponent {
  readonly booking = input.required<OwnBooking>();
  readonly resourceName = input.required<string>();
  readonly compact = input(false);

  readonly cancelRequested = output<OwnBooking>();

  protected readonly statusPillClass = statusPillClass;
  protected readonly statusLabel = statusLabel;

  protected formatRange(startUtc: string, endUtc: string): string {
    const start = toLocalDateTime(startUtc);
    const end = toLocalDateTime(endUtc);
    const sameDay = start.hasSame(end, 'day');
    return sameDay
      ? `${start.toFormat('cccc, LLL d · HH:mm')}–${end.toFormat('HH:mm')}`
      : `${start.toFormat('cccc, LLL d, HH:mm')} – ${end.toFormat('cccc, LLL d, HH:mm')}`;
  }

  protected canCancel(): boolean {
    return canCancelBooking(this.booking().status);
  }

  protected requestCancel(): void {
    this.cancelRequested.emit(this.booking());
  }
}
