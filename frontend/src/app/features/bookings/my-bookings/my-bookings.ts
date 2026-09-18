import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceService } from '../../resources/resource.service';
import { CalendarService } from '../../calendar/calendar.service';
import { CancelBookingDialogComponent } from '../cancel-booking-dialog/cancel-booking-dialog';
import { statusLabel, statusPillClass } from '../booking-status.util';
import { BookingService } from '../booking.service';
import { OwnBooking } from '../booking.models';
import { toLocalDateTime } from '../local-time.util';

const PAGE_SIZE = 10;
const CANCELLABLE_STATUSES: OwnBooking['status'][] = ['Pending', 'Confirmed'];

@Component({
  selector: 'app-my-bookings',
  imports: [RouterLink, CancelBookingDialogComponent],
  templateUrl: './my-bookings.html',
  styleUrl: './my-bookings.scss',
})
export class MyBookingsComponent {
  private readonly bookingService = inject(BookingService);
  private readonly resourceService = inject(ResourceService);
  private readonly calendarService = inject(CalendarService);
  private readonly notificationService = inject(NotificationService);

  protected readonly bookings = signal<OwnBooking[]>([]);
  protected readonly resourceNames = signal<Record<string, string>>({});
  protected readonly totalCount = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly cancelTarget = signal<OwnBooking | null>(null);
  protected readonly cancelSubmitting = signal(false);

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  constructor() {
    this.load();
  }

  protected statusPillClass = statusPillClass;
  protected statusLabel = statusLabel;

  protected resourceNameFor(resourceId: string): string {
    return this.resourceNames()[resourceId] ?? 'Loading…';
  }

  protected formatRange(startUtc: string, endUtc: string): string {
    const start = toLocalDateTime(startUtc);
    const end = toLocalDateTime(endUtc);
    const sameDay = start.hasSame(end, 'day');
    return sameDay
      ? `${start.toFormat('cccc, LLL d · HH:mm')}–${end.toFormat('HH:mm')}`
      : `${start.toFormat('cccc, LLL d, HH:mm')} – ${end.toFormat('cccc, LLL d, HH:mm')}`;
  }

  protected canCancel(booking: OwnBooking): boolean {
    return CANCELLABLE_STATUSES.includes(booking.status);
  }

  protected goToPage(page: number): void {
    if (page < 1 || page > this.totalPages()) {
      return;
    }
    this.page.set(page);
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected openCancelDialog(booking: OwnBooking): void {
    this.cancelTarget.set(booking);
  }

  protected closeCancelDialog(): void {
    this.cancelTarget.set(null);
  }

  protected confirmCancel(decision: { reason?: string; cancelRemainingSeries: boolean }): void {
    const target = this.cancelTarget();
    if (!target) {
      return;
    }

    this.cancelSubmitting.set(true);
    this.bookingService.cancelBooking(target.id, decision.cancelRemainingSeries, decision.reason).subscribe({
      next: () => {
        this.cancelSubmitting.set(false);
        this.cancelTarget.set(null);
        this.calendarService.invalidate();
        this.notificationService.showSuccess('Booking cancelled.');
        this.load();
      },
      error: () => {
        // errorInterceptor already surfaces a toast for this failure (no field errors on a cancel
        // conflict, e.g. "already past its cancellable state") - this just re-enables the dialog.
        this.cancelSubmitting.set(false);
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.bookingService.getOwnBookings(this.page(), PAGE_SIZE).subscribe({
      next: (result) => {
        this.bookings.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
        this.loadResourceNames(result.items);
      },
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loading.set(false);
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
