import { Component, input, output } from '@angular/core';
import { canCancelBooking, statusLabel, statusPillClass } from '../../booking-status.util';
import { OwnBooking } from '../../booking.models';
import { DualZoneRange, detectViewerTimeZone, formatDualZoneRange } from '../../local-time.util';

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

  protected readonly viewerZoneId = detectViewerTimeZone();

  // Resource-local time is primary (this row IS a specific booking of a specific resource), the viewer's
  // own local time is a secondary line shown only when it differs. alwaysShowDate: true because this row
  // renders inside a flat, multi-day list (My Bookings' plain rows, or an expanded recurring series'
  // occurrences) with no other day-grouping context - a bare "10:00–11:00" would make two bookings weeks
  // apart at the same time of day indistinguishable.
  protected range(): DualZoneRange {
    const booking = this.booking();
    return formatDualZoneRange(booking.startUtc, booking.endUtc, booking.timeZoneId, this.viewerZoneId, { alwaysShowDate: true });
  }

  protected canCancel(): boolean {
    return canCancelBooking(this.booking().status);
  }

  protected requestCancel(): void {
    this.cancelRequested.emit(this.booking());
  }
}
