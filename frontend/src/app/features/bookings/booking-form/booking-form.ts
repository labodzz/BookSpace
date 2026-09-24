import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { EMPTY, catchError, filter, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { CalendarService } from '../../calendar/calendar.service';
import { BookableSlot, ResourceAvailability, ResourceSummary } from '../../resources/resource.models';
import { ResourceService } from '../../resources/resource.service';
import { resolveLuxonZone } from '../../resources/timezone.util';
import { BookingService } from '../booking.service';
import { IntervalValidationResult, validateInterval } from '../availability-interval.util';
import { DualZoneRange, detectViewerTimeZone, formatDualZoneRange, parseStrictLocalDateTime } from '../local-time.util';

// Friendlier copy than the raw ProblemDetails.detail for the specific conflicts CreateBookingCommandRequest
// can throw - falls back to apiError.detail/title for anything not in this map.
const CONFLICT_MESSAGES: Record<string, string> = {
  'Booking.NoApproverConfigured': 'This resource requires approval, but no approver is assigned to it yet. Contact your administrator.',
  'Booking.ResourceUnavailable': 'This resource is not available for booking right now.',
  'Booking.OutsideAvailability': "The selected time falls outside this resource's open hours.",
  'Booking.BlackoutConflict': 'The selected time overlaps a blackout period for this resource.',
  'Booking.CapacityExceeded': "There isn't enough remaining capacity for the selected time and quantity.",
};

// Of the errors above, these specifically mean "someone/something else changed availability for this
// exact time between when the page loaded and when Confirm was pressed" - the live client-side check
// below (see `liveValidation`) exists precisely to catch this class of problem before submit, but a race
// can still slip through between the last availability fetch and the create request actually committing
// (see docs/bookings-and-concurrency.md's "read model, not a guarantee" caveat). For these, the form
// reloads availability and points the user at a fresh choice rather than leaving them staring at a
// message with no obvious next step. `Booking.ResourceUnavailable` is deliberately excluded - that means
// the RESOURCE itself changed state (e.g. archived), not that this one slot got taken, so reloading
// today's availability wouldn't clarify anything.
const TIME_CONFLICT_CODES = new Set(['Booking.OutsideAvailability', 'Booking.BlackoutConflict', 'Booking.CapacityExceeded']);

const LIVE_VALIDATION_MESSAGES: Record<IntervalValidationResult['status'], string> = {
  valid: '',
  'end-before-start': 'End time must be after start time.',
  'outside-availability': "This time is outside the resource's availability.",
  'blackout-conflict': 'This time falls within a blackout period.',
  'booking-conflict': 'This time overlaps an existing booking.',
};

// One-off booking creation, with browse-availability and confirm merged into this one page - reached
// either directly from a resource's "Book this resource" action or from the Availability page's own
// "Book" action on a specific slot (which prefills start/end via query params; those are always
// re-validated against a fresh fetch here, never trusted blindly - see the constructor's date pipeline).
// resourceId always comes from the URL, never picked inside this form, since "select a resource" is
// already the Resource List/Detail screens' job.
@Component({
  selector: 'app-booking-form',
  imports: [RouterLink, FormsModule],
  templateUrl: './booking-form.html',
  styleUrl: './booking-form.scss',
})
export class BookingFormComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly resourceService = inject(ResourceService);
  private readonly bookingService = inject(BookingService);
  private readonly calendarService = inject(CalendarService);
  private readonly notificationService = inject(NotificationService);

  protected readonly resourceId = this.route.snapshot.queryParamMap.get('resourceId') ?? '';
  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly loadingResource = signal(false);
  protected readonly resourceError = signal<ApiError | null>(null);
  // A 404 here is permanent (or a resourceId that was never real to begin with) - retrying can't help,
  // so the form offers a way back to browsing instead of a dead-end "Try again" button.
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);

  protected readonly date = signal('');
  protected readonly startTime = signal('');
  protected readonly endTime = signal('');
  protected readonly quantity = signal(1);

  // The selected date's own availability - fetched fresh every time `date` changes (never a wider
  // range, never cached across dates - see the constructor's switchMap pipeline below) so this never
  // shows stale data and never over-fetches beyond what the one visible day needs.
  protected readonly dayAvailability = signal<ResourceAvailability | null>(null);
  protected readonly availabilityLoading = signal(false);
  protected readonly availabilityError = signal<ApiError | null>(null);
  protected readonly noAvailabilityRules = computed(() => this.dayAvailability()?.openPeriods.length === 0);
  protected readonly dayFullyBooked = computed(() => {
    const day = this.dayAvailability();
    return !!day && day.openPeriods.length > 0 && day.bookableSlots.length === 0;
  });

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  // Set only for the "someone else took this exact time between load and submit" race - see
  // TIME_CONFLICT_CODES and handleError() - so the template can style it distinctly from an ordinary
  // validation message and know availability was just silently refreshed underneath the user.
  protected readonly conflictNotice = signal(false);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly timeFieldErrors = computed(() => [...(this.fieldErrors()['StartUtc'] ?? []), ...(this.fieldErrors()['EndUtc'] ?? [])]);
  protected readonly quantityFieldErrors = computed(() => this.fieldErrors()['Quantity'] ?? []);

  protected readonly viewerZoneId = detectViewerTimeZone();

  // This IS the confirmation moment for the one-off booking flow: there's no separate confirmation
  // screen (success just toasts and redirects to My Bookings) - the review of the exact selected interval,
  // right before "Confirm booking" is clicked, is where dual-zone clarity actually matters. Resource-local
  // is primary, matching the header hint above it ("Times are in {{ resource.timeZoneId }}"); the viewer's
  // own local time is the secondary line, shown only when it differs. null until a valid, fully-entered
  // date/start/end exists (the same validity check submit() itself relies on via parseStrictLocalDateTime).
  protected readonly confirmationRange = computed<DualZoneRange | null>(() => {
    const resource = this.resource();
    if (!resource || !this.date() || !this.startTime() || !this.endTime()) {
      return null;
    }

    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = parseStrictLocalDateTime(`${this.date()}T${this.startTime()}`, zone);
    const end = parseStrictLocalDateTime(`${this.date()}T${this.endTime()}`, zone);
    if (!start || !end || end <= start) {
      return null;
    }

    return formatDualZoneRange(start.toUTC().toISO()!, end.toUTC().toISO()!, resource.timeZoneId, this.viewerZoneId);
  });

  // Re-derived from the exact same raw data GetResourceAvailabilityQueryHandler returned, every time
  // date/startTime/endTime/quantity/dayAvailability change - purely a UX aid (see availability-interval
  // .util.ts's own header comment). null means "not enough loaded yet to judge" (still fetching, or the
  // fields aren't all filled in), not "valid" - submit() only trusts an explicit 'valid' result.
  protected readonly liveValidation = computed<IntervalValidationResult | null>(() => {
    const resource = this.resource();
    const day = this.dayAvailability();
    if (!resource || !day || !this.date() || !this.startTime() || !this.endTime()) {
      return null;
    }

    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = parseStrictLocalDateTime(`${this.date()}T${this.startTime()}`, zone);
    const end = parseStrictLocalDateTime(`${this.date()}T${this.endTime()}`, zone);
    if (!start || !end) {
      return null; // handled by its own, more specific nonexistent-local-time message below
    }

    return validateInterval(
      { openPeriods: day.openPeriods, blackouts: day.blackouts, busyPeriods: day.busyPeriods, capacity: day.capacity },
      start,
      end,
      this.quantity(),
    );
  });

  protected readonly nonexistentLocalTime = computed(() => {
    const resource = this.resource();
    if (!resource || !this.date() || !this.startTime() || !this.endTime()) {
      return false;
    }
    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = parseStrictLocalDateTime(`${this.date()}T${this.startTime()}`, zone);
    const end = parseStrictLocalDateTime(`${this.date()}T${this.endTime()}`, zone);
    return !start || !end;
  });

  protected readonly liveValidationMessage = computed(() => {
    if (this.nonexistentLocalTime()) {
      return "That date and time doesn't exist in this resource's timezone (likely a daylight-saving time change). Please choose a different time.";
    }
    const result = this.liveValidation();
    return result && result.status !== 'valid' ? LIVE_VALIDATION_MESSAGES[result.status] : null;
  });

  constructor() {
    if (this.resourceId) {
      this.loadResource();
    }

    // Re-fetches this one day's availability every time the date actually changes - switchMap cancels
    // whatever the previous date's request was still doing, so a slow response for a date the user has
    // already navigated away from can never land after (and overwrite) a newer one. Waits for the
    // resource to be loaded first (both the initial load and prefill() below set `date` only once
    // `resource` is already set, so in practice this only ever skips the very first, pre-resource tick).
    toObservable(this.date)
      .pipe(
        filter((date) => !!date && !!this.resource()),
        switchMap((date) => {
          this.availabilityLoading.set(true);
          this.availabilityError.set(null);
          return this.resourceService.getAvailability(this.resource()!.id, date, date).pipe(
            catchError((error: unknown) => {
              this.availabilityLoading.set(false);
              this.availabilityError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((availability) => {
        this.dayAvailability.set(availability);
        this.availabilityLoading.set(false);
      });
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  protected retryLoadAvailability(): void {
    this.refreshAvailability();
  }

  protected selectSlot(slot: BookableSlot): void {
    const resource = this.resource();
    if (!resource) {
      return;
    }
    const zone = resolveLuxonZone(resource.timeZoneId);
    this.startTime.set(DateTime.fromISO(slot.startUtc, { zone: 'utc' }).setZone(zone).toFormat('HH:mm'));
    this.endTime.set(DateTime.fromISO(slot.endUtc, { zone: 'utc' }).setZone(zone).toFormat('HH:mm'));
  }

  protected isSlotSelected(slot: BookableSlot): boolean {
    const resource = this.resource();
    if (!resource) {
      return false;
    }
    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = DateTime.fromISO(slot.startUtc, { zone: 'utc' }).setZone(zone).toFormat('HH:mm');
    const end = DateTime.fromISO(slot.endUtc, { zone: 'utc' }).setZone(zone).toFormat('HH:mm');
    return start === this.startTime() && end === this.endTime();
  }

  protected formatSlotTime(slot: BookableSlot): string {
    const resource = this.resource();
    const zone = resolveLuxonZone(resource?.timeZoneId ?? 'utc');
    const start = DateTime.fromISO(slot.startUtc, { zone: 'utc' }).setZone(zone);
    const end = DateTime.fromISO(slot.endUtc, { zone: 'utc' }).setZone(zone);
    return `${start.toFormat('HH:mm')}–${end.toFormat('HH:mm')}`;
  }

  protected formatBlackoutTime(blackout: { startUtc: string; endUtc: string }): string {
    const resource = this.resource();
    const zone = resolveLuxonZone(resource?.timeZoneId ?? 'utc');
    const start = DateTime.fromISO(blackout.startUtc, { zone: 'utc' }).setZone(zone);
    const end = DateTime.fromISO(blackout.endUtc, { zone: 'utc' }).setZone(zone);
    return `${start.toFormat('HH:mm')}–${end.toFormat('HH:mm')}`;
  }

  protected submit(): void {
    const resource = this.resource();
    if (!resource || this.submitting()) {
      return;
    }

    this.formError.set(null);
    this.conflictNotice.set(false);
    this.fieldErrors.set({});

    if (!this.date() || !this.startTime() || !this.endTime()) {
      this.formError.set('Please fill in the date and both times.');
      return;
    }

    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = parseStrictLocalDateTime(`${this.date()}T${this.startTime()}`, zone);
    const end = parseStrictLocalDateTime(`${this.date()}T${this.endTime()}`, zone);

    if (!start || !end) {
      // Most likely cause of a null here: a spring-forward daylight-saving change means this exact
      // wall-clock time never happened in the resource's timezone - see parseStrictLocalDateTime.
      this.formError.set(
        "That date and time doesn't exist in this resource's timezone (likely a daylight-saving time change). Please choose a different time.",
      );
      return;
    }
    if (end <= start) {
      this.formError.set('End time must be after the start time.');
      return;
    }
    if (start < DateTime.now()) {
      this.formError.set('Start time must be in the future.');
      return;
    }
    if (this.quantity() < 1) {
      this.formError.set('Quantity must be at least 1.');
      return;
    }

    // A definitive (non-null, non-'valid') live-validation result means this exact interval is known to
    // be unbookable against the currently-loaded availability - block here rather than round-tripping to
    // the backend just to get the same answer back. A null result (still loading, or availability
    // couldn't be fetched) is NOT treated as a block - the backend remains the final authority either way.
    const validation = this.liveValidation();
    if (validation && validation.status !== 'valid') {
      this.formError.set(LIVE_VALIDATION_MESSAGES[validation.status]);
      return;
    }

    this.submitting.set(true);
    this.bookingService
      .createBooking({
        resourceId: resource.id,
        startUtc: start.toUTC().toISO()!,
        endUtc: end.toUTC().toISO()!,
        quantity: this.quantity(),
      })
      .subscribe({
        next: (response) => {
          this.submitting.set(false);
          this.calendarService.invalidate();
          this.notificationService.showSuccess(
            response.status === 'Pending' ? 'Booking request submitted - awaiting approval.' : 'Booking confirmed.',
          );
          this.router.navigate(['/bookings']);
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
        this.prefill(resource);
      },
      error: (error: unknown) => {
        this.resourceError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loadingResource.set(false);
      },
    });
  }

  private prefill(resource: ResourceSummary): void {
    const zone = resolveLuxonZone(resource.timeZoneId);
    const startParam = this.route.snapshot.queryParamMap.get('start');
    const endParam = this.route.snapshot.queryParamMap.get('end');

    if (startParam) {
      const start = DateTime.fromISO(startParam, { zone: 'utc' }).setZone(zone);
      if (start.isValid) {
        this.date.set(start.toFormat('yyyy-MM-dd'));
        this.startTime.set(start.toFormat('HH:mm'));
      }
    } else {
      this.date.set(DateTime.now().setZone(zone).toFormat('yyyy-MM-dd'));
    }

    if (endParam) {
      const end = DateTime.fromISO(endParam, { zone: 'utc' }).setZone(zone);
      if (end.isValid) {
        this.endTime.set(end.toFormat('HH:mm'));
      }
    }
    // Deliberately NOT trusted as-is: setting `date` above feeds the constructor's availability pipeline,
    // which fetches fresh data for this exact date, and `liveValidation` then re-checks this exact
    // start/end against it just like any manually-entered time - see the class doc comment.
  }

  private refreshAvailability(): void {
    const resource = this.resource();
    if (!resource || !this.date()) {
      return;
    }
    this.availabilityLoading.set(true);
    this.availabilityError.set(null);
    this.resourceService.getAvailability(resource.id, this.date(), this.date()).subscribe({
      next: (availability) => {
        this.dayAvailability.set(availability);
        this.availabilityLoading.set(false);
      },
      error: (error: unknown) => {
        this.availabilityLoading.set(false);
        this.availabilityError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
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
    if (apiError.errorCode && TIME_CONFLICT_CODES.has(apiError.errorCode)) {
      this.formError.set(`${CONFLICT_MESSAGES[apiError.errorCode]} We've refreshed availability for this date - please choose a different time.`);
      this.conflictNotice.set(true);
      this.refreshAvailability();
      return;
    }
    if (apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) {
      this.formError.set(CONFLICT_MESSAGES[apiError.errorCode]);
      return;
    }
    this.formError.set(apiError.detail ?? apiError.title);
  }
}
