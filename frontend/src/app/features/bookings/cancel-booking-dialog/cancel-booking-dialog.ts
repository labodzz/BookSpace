import { Component, input, output, signal } from '@angular/core';
import { OwnBooking } from '../booking.models';
import { toLocalDateTime } from '../local-time.util';

// Shared confirmation UI for cancelling a booking - used by both My Bookings and the calendar's booking
// detail popover so the same confirm/reason/cascade behavior appears everywhere a booking can be
// cancelled from.
@Component({
  selector: 'app-cancel-booking-dialog',
  imports: [],
  templateUrl: './cancel-booking-dialog.html',
  styleUrl: './cancel-booking-dialog.scss',
})
export class CancelBookingDialogComponent {
  readonly booking = input.required<OwnBooking>();
  readonly resourceName = input('this resource');
  readonly submitting = input(false);

  readonly confirmed = output<{ reason?: string; cancelRemainingSeries: boolean }>();
  readonly closed = output<void>();

  protected readonly reason = signal('');
  protected readonly cancelRemainingSeries = signal(false);

  protected formatRange(startUtc: string, endUtc: string): string {
    const start = toLocalDateTime(startUtc);
    const end = toLocalDateTime(endUtc);
    return `${start.toFormat('cccc, LLL d · HH:mm')}–${end.toFormat('HH:mm')}`;
  }

  protected submit(): void {
    this.confirmed.emit({ reason: this.reason().trim() || undefined, cancelRemainingSeries: this.cancelRemainingSeries() });
  }
}
