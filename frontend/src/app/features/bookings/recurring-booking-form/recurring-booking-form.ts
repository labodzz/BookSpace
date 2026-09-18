import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { CalendarService } from '../../calendar/calendar.service';
import { ResourceSummary } from '../../resources/resource.models';
import { ResourceService } from '../../resources/resource.service';
import { resolveLuxonZone } from '../../resources/timezone.util';
import { BookingService } from '../booking.service';
import { parseStrictLocalDateTime } from '../local-time.util';
import { CreateRecurringSeriesResponse, RecurrenceFrequency } from '../booking.models';

const CONFLICT_MESSAGES: Record<string, string> = {
  'Booking.NoApproverConfigured': 'This resource requires approval, but no approver is assigned to it yet. Contact your administrator.',
};

// Friendly labels for the per-occurrence conflict reasons CreateRecurringSeriesResponse.Conflicts can
// report - falls back to the raw reason string for anything not listed here, so an unmapped future
// reason still shows something rather than nothing.
const OCCURRENCE_CONFLICT_LABELS: Record<string, string> = {
  'Booking.ResourceUnavailable': 'Resource unavailable',
  'Booking.OutsideAvailability': 'Outside open hours',
  'Booking.BlackoutConflict': 'Blackout period',
  'Booking.CapacityExceeded': 'Not enough capacity',
  NonexistentLocalTime: "Doesn't exist (daylight-saving clock change)",
};

const MAX_OCCURRENCES = 750;

type EndCondition = 'date' | 'count';

// Recurring booking creation. There is no day-of-week picker in the underlying API - a series is purely
// "every Interval days/weeks/months starting at StartDate," so that is the only recurrence shape this
// form offers; it deliberately does not invent a multi-day-of-week selector the backend can't honor.
@Component({
  selector: 'app-recurring-booking-form',
  imports: [RouterLink, FormsModule],
  templateUrl: './recurring-booking-form.html',
  styleUrl: './recurring-booking-form.scss',
})
export class RecurringBookingFormComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourceService = inject(ResourceService);
  private readonly bookingService = inject(BookingService);
  private readonly calendarService = inject(CalendarService);

  protected readonly resourceId = this.route.snapshot.queryParamMap.get('resourceId') ?? '';
  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly loadingResource = signal(false);
  protected readonly resourceError = signal<ApiError | null>(null);
  // A 404 here is permanent - retrying can't help, so the form offers a way back to browsing instead
  // of a dead-end "Try again" button.
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);

  protected readonly startDate = signal('');
  protected readonly startTime = signal('');
  protected readonly endTime = signal('');
  protected readonly frequency = signal<RecurrenceFrequency>('Weekly');
  protected readonly interval = signal(1);
  protected readonly endCondition = signal<EndCondition>('count');
  protected readonly endDate = signal('');
  protected readonly occurrenceCount = signal(10);
  protected readonly quantity = signal(1);

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly result = signal<CreateRecurringSeriesResponse | null>(null);

  protected readonly conflictLabels = OCCURRENCE_CONFLICT_LABELS;
  protected readonly maxOccurrences = MAX_OCCURRENCES;

  protected readonly startDateErrors = computed(() => this.fieldErrors()['StartDate'] ?? []);
  protected readonly timeErrors = computed(() => [...(this.fieldErrors()['StartTime'] ?? []), ...(this.fieldErrors()['EndTime'] ?? [])]);
  protected readonly intervalErrors = computed(() => this.fieldErrors()['Interval'] ?? []);
  protected readonly endErrors = computed(() => [...(this.fieldErrors()['EndDate'] ?? []), ...(this.fieldErrors()['OccurrenceCount'] ?? [])]);
  protected readonly quantityErrors = computed(() => this.fieldErrors()['Quantity'] ?? []);

  constructor() {
    if (this.resourceId) {
      this.loadResource();
    }
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  protected setEndCondition(condition: EndCondition): void {
    this.endCondition.set(condition);
  }

  protected submit(): void {
    const resource = this.resource();
    if (!resource) {
      return;
    }

    this.formError.set(null);
    this.fieldErrors.set({});
    this.result.set(null);

    if (!this.startDate() || !this.startTime() || !this.endTime()) {
      this.formError.set('Please fill in the start date and both times.');
      return;
    }
    if (this.endTime() <= this.startTime()) {
      this.formError.set('End time must be after the start time.');
      return;
    }
    if (this.interval() < 1) {
      this.formError.set('Repeat interval must be at least 1.');
      return;
    }
    if (this.quantity() < 1) {
      this.formError.set('Quantity must be at least 1.');
      return;
    }
    if (this.endCondition() === 'date' && !this.endDate()) {
      this.formError.set('Please choose an end date.');
      return;
    }
    if (this.endCondition() === 'count' && this.occurrenceCount() < 1) {
      this.formError.set('Number of occurrences must be at least 1.');
      return;
    }

    const zone = resolveLuxonZone(resource.timeZoneId);

    // Only the FIRST occurrence's start/end can be checked here - later occurrences depend on
    // frequency/interval math the backend owns, and it already reports a per-occurrence
    // "NonexistentLocalTime" conflict for any later one that lands in a DST gap (see
    // OCCURRENCE_CONFLICT_LABELS below). This still catches the common case - and gives immediate
    // inline feedback instead of a round trip - when the series' own start already doesn't exist.
    const firstOccurrenceStart = parseStrictLocalDateTime(`${this.startDate()}T${this.startTime()}`, zone);
    const firstOccurrenceEnd = parseStrictLocalDateTime(`${this.startDate()}T${this.endTime()}`, zone);
    if (!firstOccurrenceStart || !firstOccurrenceEnd) {
      this.formError.set(
        "The first occurrence's time doesn't exist in this resource's timezone (likely a daylight-saving time change). Please choose a different time or start date.",
      );
      return;
    }
    if (firstOccurrenceStart.startOf('day') < DateTime.now().setZone(zone).startOf('day')) {
      this.formError.set('Start date must be today or later.');
      return;
    }

    this.submitting.set(true);
    this.bookingService
      .createRecurringSeries({
        resourceId: resource.id,
        startDate: this.startDate(),
        startTime: this.startTime(),
        endTime: this.endTime(),
        frequency: this.frequency(),
        interval: this.interval(),
        endDate: this.endCondition() === 'date' ? this.endDate() : null,
        occurrenceCount: this.endCondition() === 'count' ? this.occurrenceCount() : null,
        quantity: this.quantity(),
      })
      .subscribe({
        next: (response) => {
          this.submitting.set(false);
          this.calendarService.invalidate();
          this.result.set(response);
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          this.handleError(error);
        },
      });
  }

  private loadResource(): void {
    this.loadingResource.set(true);
    this.resourceError.set(null);
    this.resourceService.getResource(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.loadingResource.set(false);
        this.startDate.set(DateTime.now().setZone(resolveLuxonZone(resource.timeZoneId)).toFormat('yyyy-MM-dd'));
      },
      error: (error: unknown) => {
        this.resourceError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loadingResource.set(false);
      },
    });
  }

  private handleError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.formError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError = toApiError(error);
    if (apiError.fieldErrors) {
      this.fieldErrors.set(apiError.fieldErrors);
      return;
    }
    if (apiError.errorCode === 'RecurringSeries.NoValidOccurrences') {
      this.formError.set('Every occurrence in this series conflicted with existing bookings, availability, or a blackout period - nothing was booked.');
      return;
    }
    if (apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) {
      this.formError.set(CONFLICT_MESSAGES[apiError.errorCode]);
      return;
    }
    this.formError.set(apiError.detail ?? apiError.title);
  }
}
