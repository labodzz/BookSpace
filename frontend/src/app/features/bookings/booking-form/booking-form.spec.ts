import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { BookingFormComponent } from './booking-form';

const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;
const AVAILABILITY_URL = `${RESOURCE_URL}/availability`;
const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

function setInputValue(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

interface AvailabilityOverrides {
  timeZoneId?: string;
  capacity?: number;
  openPeriods?: { startUtc: string; endUtc: string }[];
  blackouts?: { startUtc: string; endUtc: string; reason: string }[];
  busyPeriods?: { startUtc: string; endUtc: string; quantity: number }[];
  bookableSlots?: { startUtc: string; endUtc: string; availableCapacity: number }[];
}

describe('BookingFormComponent', () => {
  let fixture: ComponentFixture<BookingFormComponent>;
  let httpTesting: HttpTestingController;
  let router: Router;

  function setup(queryParams: Record<string, string>): void {
    TestBed.configureTestingModule({
      imports: [BookingFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(BookingFormComponent);
  }

  function flushResource(overrides: Partial<{ capacity: number; requiresApproval: boolean; timeZoneId: string }> = {}): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: overrides.capacity ?? 4,
      requiresApproval: overrides.requiresApproval ?? false,
      status: 0, // ResourceStatus.Active - the API serializes enums as raw numbers, not names
      timeZoneId: overrides.timeZoneId ?? 'UTC',
    });
  }

  // Deliberately a huge, permissive window by default (year 2000 to year 2100) - tests that aren't
  // specifically exercising availability edge cases don't need to reason about exact UTC day boundaries
  // for whatever local date/timezone combination they use.
  function flushAvailability(overrides: AvailabilityOverrides = {}): void {
    const req = httpTesting.expectOne((r) => r.url === AVAILABILITY_URL);
    const from = req.request.params.get('from')!;
    req.flush({
      resourceId: 'resource-1',
      fromDate: from,
      toDate: from,
      timeZoneId: overrides.timeZoneId ?? 'UTC',
      capacity: overrides.capacity ?? 4,
      openPeriods: overrides.openPeriods ?? [{ startUtc: '2000-01-01T00:00:00Z', endUtc: '2100-01-01T00:00:00Z' }],
      blackouts: overrides.blackouts ?? [],
      busyPeriods: overrides.busyPeriods ?? [],
      bookableSlots: overrides.bookableSlots ?? [],
    });
  }

  function fillValidFutureBooking(root: HTMLElement): void {
    setInputValue(root.querySelector('#date')!, '2026-12-20');
    fixture.detectChanges();
    flushAvailability(); // the date change above triggers a fresh availability fetch for 2026-12-20
    setInputValue(root.querySelector('#start-time')!, '10:00');
    setInputValue(root.querySelector('#end-time')!, '11:00');
    fixture.detectChanges();
  }

  // Every test that reaches a rendered form triggers an availability fetch for whatever date prefill()
  // set (today's date in the resource's zone, unless start/end query params say otherwise) purely as a
  // side effect of the constructor's reactive pipeline - flushed here with the generous default so
  // afterEach's httpTesting.verify() never complains about it, even in tests that don't care about it.
  function loadResourceAndInitialAvailability(overrides: Parameters<typeof flushResource>[0] = {}): HTMLElement {
    flushResource(overrides);
    fixture.detectChanges();
    flushAvailability({ timeZoneId: overrides.timeZoneId });
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => httpTesting.verify());

  it('prompts to pick a resource when no resourceId is in the URL', () => {
    setup({});
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Pick a resource to book');
  });

  it('blocks submission client-side when end time is not after start time, without calling the API', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability();

    setInputValue(root.querySelector('#date')!, '2026-12-20');
    fixture.detectChanges();
    flushAvailability();
    setInputValue(root.querySelector('#start-time')!, '10:00');
    setInputValue(root.querySelector('#end-time')!, '09:00');
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    expect(root.textContent).toContain('End time must be after the start time.');
  });

  it('submits a valid booking, shows a confirmation, and navigates to My Bookings', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability();
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.body).toEqual({
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00.000Z',
      endUtc: '2026-12-20T11:00:00.000Z',
      quantity: 1,
    });
    // status: 1 is BookingStatus.Confirmed on the wire (raw numeric enum code).
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, quantity: 1, status: 1 });

    expect(router.navigate).toHaveBeenCalledWith(['/bookings']);
  });

  it('does not submit twice for a rapid double click - only one booking request is ever sent', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability();
    fillValidFutureBooking(root);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, quantity: 1, status: 1 });
  });

  it('maps a 400 field validation error onto the quantity field instead of a generic banner', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability();
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush(
      { title: 'Validation failed', errors: { Quantity: ['Quantity must be at least 1.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(root.textContent).toContain('Quantity must be at least 1.');
  });

  // America/New_York springs forward on 2027-03-14: clocks jump from 2:00 to 3:00, so every wall-clock
  // time in [2:00, 3:00) that day never happens. Luxon silently normalizes it instead of marking it
  // invalid (see local-time.util.ts), so the form must catch this itself via a round-trip check.
  it('rejects a start time that does not exist because of a spring-forward daylight-saving change', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability({ timeZoneId: 'America/New_York' });

    setInputValue(root.querySelector('#date')!, '2027-03-14');
    fixture.detectChanges();
    flushAvailability({ timeZoneId: 'America/New_York' });
    setInputValue(root.querySelector('#start-time')!, '02:30');
    setInputValue(root.querySelector('#end-time')!, '04:00');
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    expect(root.textContent).toContain('daylight-saving time change');
  });

  // America/New_York falls back on 2027-11-07: clocks go from 2:00 to 1:00, so 1:30 AM happens twice.
  // Both occurrences are real, valid instants - this must submit normally, not be rejected the way a
  // nonexistent spring-forward time is.
  it('accepts an ambiguous fall-back local time and submits normally', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability({ timeZoneId: 'America/New_York' });

    setInputValue(root.querySelector('#date')!, '2027-11-07');
    fixture.detectChanges();
    flushAvailability({ timeZoneId: 'America/New_York' });
    setInputValue(root.querySelector('#start-time')!, '01:30');
    setInputValue(root.querySelector('#end-time')!, '03:00');
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    // 01:30 America/New_York on the fall-back day resolves to the offset in effect just BEFORE the
    // transition (EDT, -04:00) - 05:30 UTC.
    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.body.startUtc).toBe('2027-11-07T05:30:00.000Z');
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, quantity: 1, status: 1 });
  });

  // A resource's configured timezone differs from whatever zone the test runner's own clock is in -
  // the conversion to UTC must still go through the RESOURCE's zone, not the browser's.
  it('converts local input to UTC using the resource timezone, not the browser timezone', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability({ timeZoneId: 'America/New_York' });
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    // 2026-12-20 is standard time in America/New_York (EST, -05:00) - 10:00 local is 15:00 UTC.
    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.body.startUtc).toBe('2026-12-20T15:00:00.000Z');
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, quantity: 1, status: 1 });
  });

  // An unrecognized/unmapped timeZoneId falls back to the viewer's own local zone (resolveLuxonZone) -
  // submission must still work rather than crash or silently produce an "Invalid DateTime".
  it('still submits using the local zone fallback when the resource timezone is unmapped', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability({ timeZoneId: 'Nonsense/Zone' });
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL).flush({
      id: 'booking-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00.000Z',
      endUtc: '2026-12-20T11:00:00.000Z',
      quantity: 1,
      status: 1,
    });

    expect(router.navigate).toHaveBeenCalledWith(['/bookings']);
  });

  it('maps a known conflict error code to a friendly inline message, refreshes availability, and does NOT auto-retry the booking request', () => {
    setup({ resourceId: 'resource-1' });
    const root = loadResourceAndInitialAvailability();
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL)
      .flush({ title: 'Conflict', detail: 'raw detail', errorCode: 'Booking.BlackoutConflict' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain('overlaps a blackout period');
    httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    // Booking.BlackoutConflict is a "time conflict" code - handleError() reloads availability for the
    // same date automatically, so that request must still be flushed here.
    flushAvailability();
  });

  describe('live availability display and pre-submit validation', () => {
    it('fetches availability only for the selected date, not a wider range', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();

      const req = httpTesting.expectOne((r) => r.url === AVAILABILITY_URL);
      expect(req.request.params.get('from')).toBe(req.request.params.get('to'));
      req.flush({
        resourceId: 'resource-1', fromDate: req.request.params.get('from'), toDate: req.request.params.get('from'),
        timeZoneId: 'UTC', capacity: 4, openPeriods: [], blackouts: [], busyPeriods: [], bookableSlots: [],
      });
    });

    it('changing the date never lets a stale, late-arriving response for the previous date overwrite the new one', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;

      // Today's request goes out but is deliberately left unflushed here.
      const staleReq = httpTesting.expectOne((r) => r.url === AVAILABILITY_URL);

      setInputValue(root.querySelector('#date')!, '2026-12-20');
      fixture.detectChanges();

      const freshReq = httpTesting.expectOne((r) => r.url === AVAILABILITY_URL && r.params.get('from') === '2026-12-20');
      freshReq.flush({
        resourceId: 'resource-1', fromDate: '2026-12-20', toDate: '2026-12-20', timeZoneId: 'UTC', capacity: 4,
        openPeriods: [{ startUtc: '2000-01-01T00:00:00Z', endUtc: '2100-01-01T00:00:00Z' }], blackouts: [], busyPeriods: [],
        bookableSlots: [{ startUtc: '2026-12-20T10:00:00Z', endUtc: '2026-12-20T11:00:00Z', availableCapacity: 3 }],
      });
      fixture.detectChanges();
      expect(root.textContent).toContain('10:00–11:00');

      // The stale request for the ABANDONED date finally "arrives" late, with completely different
      // data. switchMap must have already cancelled its subscription, so this either throws (the
      // response is discarded outright) or is silently ignored - either way, what matters is asserted
      // below: the already-displayed 2026-12-20 data must never be replaced by it.
      try {
        staleReq.flush({
          resourceId: 'resource-1', fromDate: staleReq.request.params.get('from'), toDate: staleReq.request.params.get('from'),
          timeZoneId: 'UTC', capacity: 4, openPeriods: [], blackouts: [], busyPeriods: [
            { startUtc: '2000-01-01T00:00:00Z', endUtc: '2100-01-01T00:00:00Z' },
          ],
        });
      } catch {
        // Expected when switchMap has already torn down this request's subscription.
      }
      fixture.detectChanges();

      expect(root.textContent).toContain('10:00–11:00');
    });

    it("shows the resource's timezone and uses it for the UTC conversion", () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability({ timeZoneId: 'Europe/Sarajevo' });

      expect(root.textContent).toContain('Europe/Sarajevo');
    });

    it('clicking a free interval fills in Start and End', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();
      flushAvailability({ bookableSlots: [{ startUtc: '2026-01-01T09:00:00Z', availableCapacity: 2, endUtc: '2026-01-01T12:00:00Z' }] });
      fixture.detectChanges();

      const root = fixture.nativeElement as HTMLElement;
      const slot = root.querySelector('.booking-form__slot') as HTMLButtonElement;
      expect(slot).not.toBeNull();
      slot.click();
      fixture.detectChanges();

      expect((root.querySelector('#start-time') as HTMLInputElement).value).toBe('09:00');
      expect((root.querySelector('#end-time') as HTMLInputElement).value).toBe('12:00');
      expect(slot.getAttribute('aria-pressed')).toBe('true');
    });

    it('rejects (before submit) an interval outside the resource\'s open hours', () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability({
        capacity: 4,
      });
      // Override with a narrow open period for the freshly-set date below.
      setInputValue(root.querySelector('#date')!, '2026-06-01');
      fixture.detectChanges();
      flushAvailability({ openPeriods: [{ startUtc: '2026-06-01T09:00:00Z', endUtc: '2026-06-01T17:00:00Z' }] });
      fixture.detectChanges();

      setInputValue(root.querySelector('#start-time')!, '18:00');
      setInputValue(root.querySelector('#end-time')!, '19:00');
      fixture.detectChanges();

      expect(root.textContent).toContain("This time is outside the resource's availability.");
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    });

    it('rejects (before submit) an interval overlapping an existing booking (capacity)', () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability({ capacity: 1 });
      setInputValue(root.querySelector('#date')!, '2026-06-01');
      fixture.detectChanges();
      flushAvailability({
        capacity: 1,
        openPeriods: [{ startUtc: '2026-06-01T09:00:00Z', endUtc: '2026-06-01T17:00:00Z' }],
        busyPeriods: [{ startUtc: '2026-06-01T10:00:00Z', endUtc: '2026-06-01T11:00:00Z', quantity: 1 }],
      });
      fixture.detectChanges();

      setInputValue(root.querySelector('#start-time')!, '10:30');
      setInputValue(root.querySelector('#end-time')!, '10:45');
      fixture.detectChanges();

      expect(root.textContent).toContain('This time overlaps an existing booking.');
      root.querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    });

    it('rejects (before submit) an interval overlapping a blackout period', () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability();
      setInputValue(root.querySelector('#date')!, '2026-06-01');
      fixture.detectChanges();
      flushAvailability({
        openPeriods: [{ startUtc: '2026-06-01T09:00:00Z', endUtc: '2026-06-01T17:00:00Z' }],
        blackouts: [{ startUtc: '2026-06-01T12:00:00Z', endUtc: '2026-06-01T13:00:00Z', reason: 'Cleaning' }],
      });
      fixture.detectChanges();

      setInputValue(root.querySelector('#start-time')!, '12:15');
      setInputValue(root.querySelector('#end-time')!, '12:45');
      fixture.detectChanges();

      expect(root.textContent).toContain('This time falls within a blackout period.');
      expect(root.textContent).toContain('Cleaning');
    });

    it('does not flag a booking ending exactly when the requested interval starts - the half-open boundary', () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability({ capacity: 1 });
      setInputValue(root.querySelector('#date')!, '2026-06-01');
      fixture.detectChanges();
      flushAvailability({
        capacity: 1,
        openPeriods: [{ startUtc: '2026-06-01T09:00:00Z', endUtc: '2026-06-01T17:00:00Z' }],
        busyPeriods: [{ startUtc: '2026-06-01T09:00:00Z', endUtc: '2026-06-01T10:00:00Z', quantity: 1 }],
      });
      fixture.detectChanges();

      setInputValue(root.querySelector('#start-time')!, '10:00');
      setInputValue(root.querySelector('#end-time')!, '11:00');
      fixture.detectChanges();

      expect(root.textContent).not.toContain('This time overlaps an existing booking.');
    });

    it('shows "fully booked" when the day has open hours but no bookable slots remain', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();
      flushAvailability({ openPeriods: [{ startUtc: '2026-01-01T09:00:00Z', endUtc: '2026-01-01T17:00:00Z' }], bookableSlots: [] });
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('This day is fully booked');
    });

    it('shows a distinct message when the day has no availability rules at all', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();
      flushAvailability({ openPeriods: [] });
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).textContent).toContain('No availability rules are configured for this day');
    });

    it('shows a retry button when the availability fetch fails, and retry re-issues the request', () => {
      setup({ resourceId: 'resource-1' });
      flushResource();
      fixture.detectChanges();
      httpTesting.expectOne((r) => r.url === AVAILABILITY_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
      fixture.detectChanges();

      const root = fixture.nativeElement as HTMLElement;
      expect(root.textContent).toContain("Couldn't load availability");
      const retryButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Try again')!;
      retryButton.click();
      fixture.detectChanges();

      flushAvailability();
      fixture.detectChanges();
      expect(root.textContent).not.toContain("Couldn't load availability");
    });

    it('on a backend booking conflict, preserves the form, reloads availability, and shows an inline notice - without auto-retrying', () => {
      setup({ resourceId: 'resource-1' });
      const root = loadResourceAndInitialAvailability({ capacity: 4 });
      fillValidFutureBooking(root);
      root.querySelector('form')!.dispatchEvent(new Event('submit'));

      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL)
        .flush({ title: 'Conflict', detail: 'raw', errorCode: 'Booking.CapacityExceeded' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      // Form values are untouched.
      expect((root.querySelector('#date') as HTMLInputElement).value).toBe('2026-12-20');
      expect((root.querySelector('#start-time') as HTMLInputElement).value).toBe('10:00');
      expect(root.textContent).toContain("We've refreshed availability");

      // Availability was reloaded (a fresh GET for the same date), not the booking request retried.
      flushAvailability();
      httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    });

    it('re-validates a prefilled slot from a query-param start/end against fresh data, rather than trusting it blindly', () => {
      setup({ resourceId: 'resource-1', start: '2026-06-01T10:00:00Z', end: '2026-06-01T11:00:00Z' });
      flushResource();
      fixture.detectChanges();
      // The prefilled window (10:00-11:00 UTC) falls OUTSIDE this response's open hours - proving the
      // query-param values are re-checked against a real fetch, not assumed valid on arrival.
      flushAvailability({ openPeriods: [{ startUtc: '2026-06-01T14:00:00Z', endUtc: '2026-06-01T18:00:00Z' }] });
      fixture.detectChanges();

      const root = fixture.nativeElement as HTMLElement;
      expect((root.querySelector('#start-time') as HTMLInputElement).value).toBe('10:00');
      expect(root.textContent).toContain("This time is outside the resource's availability.");
    });
  });
});
