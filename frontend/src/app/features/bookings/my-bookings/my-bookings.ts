import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceService } from '../../resources/resource.service';
import { CalendarService } from '../../calendar/calendar.service';
import { CancelBookingDialogComponent } from '../cancel-booking-dialog/cancel-booking-dialog';
import { BookingService } from '../booking.service';
import { OwnBooking } from '../booking.models';
import { toLocalDateTime } from '../local-time.util';
import { BookingListEntry, groupBookingsForDisplay } from './booking-grouping.util';
import { BookingRowComponent } from './booking-row/booking-row';

const PAGE_SIZE = 10;

@Component({
  selector: 'app-my-bookings',
  imports: [RouterLink, CancelBookingDialogComponent, BookingRowComponent],
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

  // Every occurrence of a series, keyed by seriesId, fetched separately from the main page (see
  // loadSeriesOccurrences) so an expanded group always shows the series' COMPLETE occurrence list, not
  // just whichever occurrences happened to land on the current page of the flat getOwnBookings listing.
  protected readonly seriesOccurrences = signal<Record<string, OwnBooking[]>>({});
  protected readonly expandedSeries = signal<ReadonlySet<string>>(new Set());

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));
  protected readonly entries = computed(() => groupBookingsForDisplay(this.bookings()));

  constructor() {
    this.load();
  }

  protected resourceNameFor(resourceId: string): string {
    return this.resourceNames()[resourceId] ?? 'Loading…';
  }

  protected entryKey(entry: BookingListEntry): string {
    return entry.kind === 'single' ? entry.booking.id : entry.seriesId;
  }

  // The occurrences to render for an expanded series group: the fully-fetched list once it has loaded,
  // falling back to whatever occurrences of it are already known from this page while that fetch is
  // still in flight (or if it failed) - so expanding never shows an empty panel.
  protected occurrencesFor(entry: Extract<BookingListEntry, { kind: 'series' }>): OwnBooking[] {
    return this.seriesOccurrences()[entry.seriesId] ?? entry.occurrences;
  }

  protected seriesSummary(entry: Extract<BookingListEntry, { kind: 'series' }>): string {
    const occurrences = this.occurrencesFor(entry);
    if (occurrences.length === 0) {
      return 'Recurring booking';
    }
    const sorted = [...occurrences].sort((a, b) => toLocalDateTime(a.startUtc).toMillis() - toLocalDateTime(b.startUtc).toMillis());
    const first = toLocalDateTime(sorted[0].startUtc);
    const last = toLocalDateTime(sorted[sorted.length - 1].startUtc);
    const range = first.hasSame(last, 'day') ? first.toFormat('LLL d, yyyy') : `${first.toFormat('LLL d')} – ${last.toFormat('LLL d, yyyy')}`;
    const count = occurrences.length;
    return `${count} ${count === 1 ? 'booking' : 'bookings'} · ${range}`;
  }

  protected isExpanded(seriesId: string): boolean {
    return this.expandedSeries().has(seriesId);
  }

  protected toggleSeries(seriesId: string): void {
    const next = new Set(this.expandedSeries());
    if (next.has(seriesId)) {
      next.delete(seriesId);
    } else {
      next.add(seriesId);
    }
    this.expandedSeries.set(next);
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
        if (target.seriesId) {
          // The cancelled-status change (and, when cancelRemainingSeries is checked, every later
          // occurrence's cascaded cancellation too) must be re-fetched, not left showing stale statuses -
          // clearing the cache here makes loadSeriesOccurrences treat it as missing again after load().
          const next = { ...this.seriesOccurrences() };
          delete next[target.seriesId];
          this.seriesOccurrences.set(next);
        }
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
        this.loadSeriesOccurrences(result.items);
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

  private loadSeriesOccurrences(bookings: OwnBooking[]): void {
    const known = this.seriesOccurrences();
    const missingSeriesIds = [...new Set(bookings.map((booking) => booking.seriesId))].filter(
      (id): id is string => id !== null && !(id in known),
    );

    missingSeriesIds.forEach((seriesId) => {
      this.bookingService.getBookingsForSeries(seriesId).subscribe({
        next: (occurrences) => {
          this.seriesOccurrences.set({ ...this.seriesOccurrences(), [seriesId]: occurrences });
        },
        // A failed enhancement fetch shouldn't break the page - occurrencesFor() already falls back to
        // this page's own occurrences of the series when nothing is cached for it.
        error: () => {},
      });
    });
  }
}
