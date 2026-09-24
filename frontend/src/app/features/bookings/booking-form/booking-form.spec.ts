import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { BookingFormComponent } from './booking-form';

const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;
const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

function setInputValue(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
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

  function fillValidFutureBooking(root: HTMLElement): void {
    setInputValue(root.querySelector('#date')!, '2026-12-20');
    setInputValue(root.querySelector('#start-time')!, '10:00');
    setInputValue(root.querySelector('#end-time')!, '11:00');
  }

  afterEach(() => httpTesting.verify());

  it('prompts to pick a resource when no resourceId is in the URL', () => {
    setup({});
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Pick a resource to book');
  });

  it('blocks submission client-side when end time is not after start time, without calling the API', () => {
    setup({ resourceId: 'resource-1' });
    flushResource();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#date')!, '2026-12-20');
    setInputValue(root.querySelector('#start-time')!, '10:00');
    setInputValue(root.querySelector('#end-time')!, '09:00');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL);
    expect(root.textContent).toContain('End time must be after the start time.');
  });

  it('submits a valid booking, shows a confirmation, and navigates to My Bookings', () => {
    setup({ resourceId: 'resource-1' });
    flushResource();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
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

  it('maps a 400 field validation error onto the quantity field instead of a generic banner', () => {
    setup({ resourceId: 'resource-1' });
    flushResource();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
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
    flushResource({ timeZoneId: 'America/New_York' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#date')!, '2027-03-14');
    setInputValue(root.querySelector('#start-time')!, '02:30');
    setInputValue(root.querySelector('#end-time')!, '04:00');
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
    flushResource({ timeZoneId: 'America/New_York' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#date')!, '2027-11-07');
    setInputValue(root.querySelector('#start-time')!, '01:30');
    setInputValue(root.querySelector('#end-time')!, '03:00');
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
    flushResource({ timeZoneId: 'America/New_York' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    fillValidFutureBooking(root);
    fixture.detectChanges();

    // The confirmation preview is purely additive display - it must never change what's actually
    // submitted (required scenario 7's frontend half).
    expect(root.querySelector('.booking-form__confirmation-range')!.textContent).toContain('10:00–11:00');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    // 2026-12-20 is standard time in America/New_York (EST, -05:00) - 10:00 local is 15:00 UTC.
    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.body.startUtc).toBe('2026-12-20T15:00:00.000Z');
    req.flush({ id: 'booking-1', resourceId: 'resource-1', startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, quantity: 1, status: 1, timeZoneId: 'America/New_York' });
  });

  // An unrecognized/unmapped timeZoneId falls back to the viewer's own local zone (resolveLuxonZone) -
  // submission must still work rather than crash or silently produce an "Invalid DateTime".
  it('still submits using the local zone fallback when the resource timezone is unmapped', () => {
    setup({ resourceId: 'resource-1' });
    flushResource({ timeZoneId: 'Nonsense/Zone' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
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

  it('maps a known conflict error code to a friendly inline message', () => {
    setup({ resourceId: 'resource-1' });
    flushResource();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    fillValidFutureBooking(root);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL)
      .flush({ title: 'Conflict', detail: 'raw detail', errorCode: 'Booking.BlackoutConflict' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain("overlaps a blackout period");
  });
});

// A separate top-level describe (not nested above) because the viewer's zone is captured once, at
// component construction (`viewerZoneId = detectViewerTimeZone()`) - the Intl mock must be in place
// BEFORE TestBed.createComponent runs, which the shared `setup()` above already does unconditionally.
describe('BookingFormComponent - dual timezone display', () => {
  let fixture: ComponentFixture<BookingFormComponent>;
  let httpTesting: HttpTestingController;

  function setup(): void {
    TestBed.configureTestingModule({
      imports: [BookingFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ resourceId: 'resource-1' }) } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(BookingFormComponent);
  }

  function flushResource(timeZoneId: string): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId,
    });
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it("shows the resource-local time as primary, and the viewer's local time as a secondary line when the zones differ", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    setup();
    flushResource('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#date')!, '2026-07-15');
    setInputValue(root.querySelector('#start-time')!, '10:15');
    setInputValue(root.querySelector('#end-time')!, '11:15');
    fixture.detectChanges();

    const range = root.querySelector('.booking-form__confirmation-range')!;
    expect(range.textContent).toContain('10:15–11:15');
    expect(range.textContent).toContain('Europe/Sarajevo');
    expect(range.textContent).toContain('Your local time: 17:15–18:15');
    expect(range.textContent).toContain('Asia/Tokyo');
  });

  it('shows only one time when the resource zone matches the viewer zone', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
    setup();
    flushResource('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#date')!, '2026-07-15');
    setInputValue(root.querySelector('#start-time')!, '10:15');
    setInputValue(root.querySelector('#end-time')!, '11:15');
    fixture.detectChanges();

    const range = root.querySelector('.booking-form__confirmation-range')!;
    expect(range.textContent).toContain('10:15–11:15');
    expect(range.textContent).not.toContain('Your local time');
  });

  it('shows no confirmation preview until the date and both times are filled in', () => {
    setup();
    flushResource('UTC');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.booking-form__confirmation-range')).toBeNull();
  });
});
