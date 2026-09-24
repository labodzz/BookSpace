import { Component, computed, effect, inject, signal } from '@angular/core';
import { DateTime } from 'luxon';
import { forkJoin } from 'rxjs';
import { NotificationService } from '../../core/notifications/notification.service';
import { CancelBookingDialogComponent } from '../bookings/cancel-booking-dialog/cancel-booking-dialog';
import { statusLabel, statusPillClass } from '../bookings/booking-status.util';
import { BookingService } from '../bookings/booking.service';
import { OwnBooking } from '../bookings/booking.models';
import { ResourceService } from '../resources/resource.service';
import { DualZoneRange, detectViewerTimeZone, formatDualZoneRange, toLocalDateTime } from '../bookings/local-time.util';
import { CalendarService } from './calendar.service';

type ViewMode = 'month' | 'week';

interface CalendarCell {
  date: DateTime<true>;
  inCurrentMonth: boolean;
  isToday: boolean;
  bookings: OwnBooking[];
}

const MONTH_CELL_EVENT_CAP = 3;
const CANCELLABLE_STATUSES: OwnBooking['status'][] = ['Pending', 'Confirmed'];

// The calendar shows the SIGNED-IN USER'S OWN bookings across every resource - GET /bookings has no
// tenant-wide, cross-user, date-ranged endpoint (only "my own bookings"), so a shared team calendar
// would need a materially larger backend feature. See the WP-7 final report for the full rationale.
//
// Performance: only the visible grid range is ever requested (CalendarService.loadRange), recurring
// occurrences arrive already expanded as individual bookings (no client-side expansion), repeat
// navigation to an already-seen range is served from CalendarService's cache, and month view caps each
// day cell to MONTH_CELL_EVENT_CAP events with a "+N more" link into Week view rather than rendering an
// unbounded number of DOM nodes for a busy day.
@Component({
  selector: 'app-calendar',
  imports: [CancelBookingDialogComponent],
  templateUrl: './calendar.html',
  styleUrl: './calendar.scss',
})
export class CalendarComponent {
  protected readonly calendarService = inject(CalendarService);
  private readonly resourceService = inject(ResourceService);
  private readonly bookingService = inject(BookingService);
  private readonly notificationService = inject(NotificationService);

  protected readonly anchor = signal<DateTime<true>>(DateTime.now().startOf('day'));
  protected readonly viewMode = signal<ViewMode>('month');
  protected readonly resourceNames = signal<Record<string, string>>({});

  protected readonly selectedBooking = signal<OwnBooking | null>(null);
  protected readonly cancelDialogOpen = signal(false);
  protected readonly cancelling = signal(false);

  protected readonly statusPillClass = statusPillClass;
  protected readonly statusLabel = statusLabel;
  protected readonly monthCellEventCap = MONTH_CELL_EVENT_CAP;
  protected readonly viewerZoneId = detectViewerTimeZone();

  protected readonly gridStart = computed(() =>
    this.viewMode() === 'month' ? this.anchor().startOf('month').startOf('week') : this.anchor().startOf('week'),
  );
  protected readonly gridEnd = computed(() =>
    (this.viewMode() === 'month' ? this.anchor().endOf('month').endOf('week') : this.anchor().endOf('week')).startOf('day'),
  );

  protected readonly title = computed(() =>
    this.viewMode() === 'month'
      ? this.anchor().toFormat('LLLL yyyy')
      : `${this.gridStart().toFormat('LLL d')} – ${this.gridEnd().toFormat('LLL d, yyyy')}`,
  );

  protected readonly cells = computed<CalendarCell[]>(() => {
    const start = this.gridStart();
    const end = this.gridEnd();
    const bookings = this.calendarService.bookings();
    const anchor = this.anchor();
    const today = DateTime.now().startOf('day');

    const cells: CalendarCell[] = [];
    for (let date = start; date <= end; date = date.plus({ days: 1 })) {
      const dayBookings = bookings
        .filter((booking) => toLocalDateTime(booking.startUtc).hasSame(date, 'day'))
        .sort((a, b) => a.startUtc.localeCompare(b.startUtc));
      cells.push({ date, inCurrentMonth: date.hasSame(anchor, 'month'), isToday: date.hasSame(today, 'day'), bookings: dayBookings });
    }
    return cells;
  });

  constructor() {
    // Re-fetches whenever the visible range changes (navigation or view-mode toggle) - and once on
    // init, since an effect always runs immediately the first time. CalendarService itself handles
    // caching and cancelling a stale in-flight request when the range changes again before it resolves.
    effect(() => {
      const from = this.gridStart();
      const to = this.gridEnd().plus({ days: 1 });
      this.calendarService.loadRange(from, to);
    });

    effect(() => this.loadResourceNames(this.calendarService.bookings()));
  }

  protected previous(): void {
    this.anchor.update((current) => (this.viewMode() === 'month' ? current.minus({ months: 1 }) : current.minus({ weeks: 1 })));
  }

  protected next(): void {
    this.anchor.update((current) => (this.viewMode() === 'month' ? current.plus({ months: 1 }) : current.plus({ weeks: 1 })));
  }

  protected today(): void {
    this.anchor.set(DateTime.now().startOf('day'));
  }

  protected setViewMode(mode: ViewMode): void {
    this.viewMode.set(mode);
  }

  protected showFullDay(date: DateTime<true>): void {
    this.anchor.set(date);
    this.viewMode.set('week');
  }

  protected retry(): void {
    const from = this.gridStart();
    const to = this.gridEnd().plus({ days: 1 });
    this.calendarService.loadRange(from, to);
  }

  protected resourceNameFor(resourceId: string): string {
    return this.resourceNames()[resourceId] ?? 'Loading…';
  }

  protected formatTime(utcIso: string): string {
    return toLocalDateTime(utcIso).toFormat('HH:mm');
  }

  // Unlike every other screen, the VIEWER's zone is first/primary here, not the resource's - the grid
  // itself already buckets and labels every event in the viewer's own zone (see `cells`/`formatTime`
  // above, deliberately untouched), so the detail dialog keeps that same frame of reference and adds the
  // resource's local time as the secondary line only when it differs.
  protected formatDetailRange(booking: OwnBooking): DualZoneRange {
    return formatDualZoneRange(booking.startUtc, booking.endUtc, this.viewerZoneId, booking.timeZoneId);
  }

  protected selectBooking(booking: OwnBooking): void {
    this.selectedBooking.set(booking);
  }

  protected closeDetail(): void {
    this.selectedBooking.set(null);
    this.cancelDialogOpen.set(false);
  }

  protected canCancel(booking: OwnBooking): boolean {
    return CANCELLABLE_STATUSES.includes(booking.status);
  }

  protected openCancelDialog(): void {
    this.cancelDialogOpen.set(true);
  }

  protected closeCancelDialog(): void {
    this.cancelDialogOpen.set(false);
  }

  protected confirmCancel(decision: { reason?: string; cancelRemainingSeries: boolean }): void {
    const target = this.selectedBooking();
    if (!target) {
      return;
    }

    this.cancelling.set(true);
    this.bookingService.cancelBooking(target.id, decision.cancelRemainingSeries, decision.reason).subscribe({
      next: () => {
        this.cancelling.set(false);
        this.cancelDialogOpen.set(false);
        this.selectedBooking.set(null);
        this.notificationService.showSuccess('Booking cancelled.');
        this.calendarService.invalidate();
        this.calendarService.loadRange(this.gridStart(), this.gridEnd().plus({ days: 1 }));
      },
      error: () => {
        this.cancelling.set(false);
      },
    });
  }

  private loadResourceNames(bookings: OwnBooking[]): void {
    const known = this.resourceNames();
    const missingIds = [...new Set(bookings.map((booking) => booking.resourceId))].filter((id) => !(id in known));
    if (missingIds.length === 0) {
      return;
    }

    forkJoin(missingIds.map((id) => this.resourceService.getResourceCached(id))).subscribe({
      next: (resources) => {
        const next = { ...this.resourceNames() };
        resources.forEach((resource) => (next[resource.id] = resource.name));
        this.resourceNames.set(next);
      },
      error: () => {
        const next = { ...this.resourceNames() };
        missingIds.forEach((id) => (next[id] = 'Unknown resource'));
        this.resourceNames.set(next);
      },
    });
  }
}
