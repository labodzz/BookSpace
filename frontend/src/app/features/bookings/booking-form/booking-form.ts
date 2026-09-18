import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { CalendarService } from '../../calendar/calendar.service';
import { ResourceSummary } from '../../resources/resource.models';
import { ResourceService } from '../../resources/resource.service';
import { resolveLuxonZone } from '../../resources/timezone.util';
import { BookingService } from '../booking.service';

// Friendlier copy than the raw ProblemDetails.detail for the specific conflicts CreateBookingCommandRequest
// can throw - falls back to apiError.detail/title for anything not in this map.
const CONFLICT_MESSAGES: Record<string, string> = {
  'Booking.NoApproverConfigured': 'This resource requires approval, but no approver is assigned to it yet. Contact your administrator.',
  'Booking.ResourceUnavailable': 'This resource is not available for booking right now.',
  'Booking.OutsideAvailability': "The selected time falls outside this resource's open hours.",
  'Booking.BlackoutConflict': 'The selected time overlaps a blackout period for this resource.',
  'Booking.CapacityExceeded': "There isn't enough remaining capacity for the selected time and quantity.",
};

// One-off booking creation. Reached either directly from a resource's "Book this resource" action or
// from an availability slot click (which prefills start/end via query params) - resourceId is always
// required and comes from the URL, never picked inside this form, since "select a resource" is already
// the Resource List/Detail screens' job.
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

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string[]>>({});
  protected readonly timeFieldErrors = computed(() => [...(this.fieldErrors()['StartUtc'] ?? []), ...(this.fieldErrors()['EndUtc'] ?? [])]);
  protected readonly quantityFieldErrors = computed(() => this.fieldErrors()['Quantity'] ?? []);

  constructor() {
    if (this.resourceId) {
      this.loadResource();
    }
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  protected submit(): void {
    const resource = this.resource();
    if (!resource) {
      return;
    }

    this.formError.set(null);
    this.fieldErrors.set({});

    if (!this.date() || !this.startTime() || !this.endTime()) {
      this.formError.set('Please fill in the date and both times.');
      return;
    }

    const zone = resolveLuxonZone(resource.timeZoneId);
    const start = DateTime.fromISO(`${this.date()}T${this.startTime()}`, { zone });
    const end = DateTime.fromISO(`${this.date()}T${this.endTime()}`, { zone });

    if (!start.isValid || !end.isValid) {
      this.formError.set('That date and time combination is not valid.');
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
    if (apiError.errorCode && CONFLICT_MESSAGES[apiError.errorCode]) {
      this.formError.set(CONFLICT_MESSAGES[apiError.errorCode]);
      return;
    }
    this.formError.set(apiError.detail ?? apiError.title);
  }
}
