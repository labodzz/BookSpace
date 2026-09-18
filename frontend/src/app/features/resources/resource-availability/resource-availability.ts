import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { BlackoutWindow, BookableSlot, ResourceAvailability, ResourceSummary } from '../resource.models';
import { ResourceService } from '../resource.service';
import { resolveLuxonZone } from '../timezone.util';

// The API caps a single availability query to a 92-day span - GetResourceAvailabilityQueryRequestValidator
// rejects a range once ToDate.DayNumber - FromDate.DayNumber reaches 92, so the largest ACCEPTED
// difference is 91 (a 92-calendar-date-inclusive span). This default window (14 days) stays comfortably
// inside that, and the date pickers below re-validate the exact same boundary client-side so a user
// gets an immediate, friendly message instead of waiting on a round trip to fail.
const DEFAULT_WINDOW_DAYS = 13;
const MAX_RANGE_DAYS = 92;

interface DayAvailability {
  date: DateTime;
  blackouts: BlackoutWindow[];
  slots: BookableSlot[];
}

@Component({
  selector: 'app-resource-availability',
  imports: [RouterLink, FormsModule],
  templateUrl: './resource-availability.html',
  styleUrl: './resource-availability.scss',
})
export class ResourceAvailabilityComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly resourceService = inject(ResourceService);

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly availability = signal<ResourceAvailability | null>(null);
  protected readonly days = signal<DayAvailability[]>([]);

  // Separate from loading()/error() below, which track the availability fetch once a resource is
  // known - these track the initial resource lookup itself, since a missing resourceId (bad link, a
  // deleted resource) must not leave the page stuck on a skeleton forever.
  protected readonly resourceLoading = signal(true);
  protected readonly resourceError = signal<ApiError | null>(null);
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);

  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly rangeError = signal<string | null>(null);

  protected readonly fromDate = signal('');
  protected readonly toDate = signal('');

  constructor() {
    this.loadResource();
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  private loadResource(): void {
    this.resourceLoading.set(true);
    this.resourceError.set(null);
    this.resourceService.getResource(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.resourceLoading.set(false);
        const today = DateTime.now().setZone(resolveLuxonZone(resource.timeZoneId)).startOf('day');
        this.fromDate.set(today.toFormat('yyyy-MM-dd'));
        this.toDate.set(today.plus({ days: DEFAULT_WINDOW_DAYS }).toFormat('yyyy-MM-dd'));
        this.load();
      },
      error: (error: unknown) => {
        this.resourceError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.resourceLoading.set(false);
      },
    });
  }

  protected onFromDateChange(event: Event): void {
    this.fromDate.set((event.target as HTMLInputElement).value);
  }

  protected onToDateChange(event: Event): void {
    this.toDate.set((event.target as HTMLInputElement).value);
  }

  protected search(): void {
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected bookSlot(slot: BookableSlot): void {
    this.router.navigate(['/bookings/new'], {
      queryParams: { resourceId: this.resourceId, start: slot.startUtc, end: slot.endUtc },
    });
  }

  protected formatDayHeading(date: DateTime): string {
    return date.toFormat('cccc, LLL d');
  }

  protected formatSlotTime(slot: BookableSlot, zone: string): string {
    const resolvedZone = resolveLuxonZone(zone);
    const start = DateTime.fromISO(slot.startUtc, { zone: 'utc' }).setZone(resolvedZone);
    const end = DateTime.fromISO(slot.endUtc, { zone: 'utc' }).setZone(resolvedZone);
    return `${start.toFormat('HH:mm')}–${end.toFormat('HH:mm')}`;
  }

  protected formatBlackoutTime(blackout: BlackoutWindow, zone: string): string {
    const resolvedZone = resolveLuxonZone(zone);
    const start = DateTime.fromISO(blackout.startUtc, { zone: 'utc' }).setZone(resolvedZone);
    const end = DateTime.fromISO(blackout.endUtc, { zone: 'utc' }).setZone(resolvedZone);
    return `${start.toFormat('HH:mm')}–${end.toFormat('HH:mm')}`;
  }

  private load(): void {
    const resource = this.resource();
    if (!resource || !this.fromDate() || !this.toDate()) {
      return;
    }

    const from = DateTime.fromISO(this.fromDate());
    const to = DateTime.fromISO(this.toDate());
    if (to < from) {
      this.rangeError.set('End date must be on or after the start date.');
      return;
    }
    // Strictly `>=`, matching the backend's `ToDate.DayNumber - FromDate.DayNumber < MaxRangeDays`: a
    // difference of exactly 92 is the first value the backend rejects, so accepting it here would let
    // the frontend submit a range the API is guaranteed to 400 on.
    if (to.diff(from, 'days').days >= MAX_RANGE_DAYS) {
      this.rangeError.set(`The date range can't span more than ${MAX_RANGE_DAYS} days.`);
      return;
    }
    this.rangeError.set(null);

    this.loading.set(true);
    this.error.set(null);
    this.resourceService.getAvailability(resource.id, this.fromDate(), this.toDate()).subscribe({
      next: (availability) => {
        this.availability.set(availability);
        this.days.set(this.buildDays(availability));
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loading.set(false);
      },
    });
  }

  private buildDays(availability: ResourceAvailability): DayAvailability[] {
    const zone = resolveLuxonZone(availability.timeZoneId);
    const start = DateTime.fromISO(availability.fromDate, { zone });
    const end = DateTime.fromISO(availability.toDate, { zone });

    const days: DayAvailability[] = [];
    for (let day = start; day <= end; day = day.plus({ days: 1 })) {
      days.push({ date: day, blackouts: [], slots: [] });
    }

    for (const slot of availability.bookableSlots) {
      const localDay = DateTime.fromISO(slot.startUtc, { zone: 'utc' }).setZone(zone).startOf('day');
      days.find((day) => day.date.hasSame(localDay, 'day'))?.slots.push(slot);
    }

    // Unlike bookable slots (which never cross midnight), a blackout can span several calendar dates -
    // it must show up on EVERY date it overlaps, not just the one its startUtc happens to fall on. For
    // each already-built (and therefore range-clipped) day, endUtc is treated as exclusive - matching
    // the backend's own half-open [Start, End) interval convention (see the range-overlap check in
    // GetResourceAvailabilityQueryHandler) - so a blackout ending exactly at a day's local midnight
    // occupies every day up to that boundary but never the day that starts at it.
    for (const blackout of availability.blackouts) {
      const localStart = DateTime.fromISO(blackout.startUtc, { zone: 'utc' }).setZone(zone);
      const localEnd = DateTime.fromISO(blackout.endUtc, { zone: 'utc' }).setZone(zone);
      for (const day of days) {
        const dayEnd = day.date.plus({ days: 1 });
        if (localStart < dayEnd && localEnd > day.date) {
          day.blackouts.push(blackout);
        }
      }
    }

    return days;
  }
}
