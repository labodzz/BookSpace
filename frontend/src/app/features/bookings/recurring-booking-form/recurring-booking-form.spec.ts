import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { RecurringBookingFormComponent } from './recurring-booking-form';

const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;
const SERIES_URL = `${environment.apiUrl}/bookings/series`;

function setInputValue(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('RecurringBookingFormComponent', () => {
  let fixture: ComponentFixture<RecurringBookingFormComponent>;
  let httpTesting: HttpTestingController;

  function setup(): void {
    TestBed.configureTestingModule({
      imports: [RecurringBookingFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ resourceId: 'resource-1' }) } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RecurringBookingFormComponent);
  }

  function flushResource(timeZoneId: string): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 0,
      timeZoneId,
    });
  }

  afterEach(() => httpTesting.verify());

  it('shows a link back to the one-time booking form, preserving resourceId', () => {
    setup();
    flushResource('UTC');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const oneTimeLink = [...root.querySelectorAll('a')].find((a) => a.textContent?.trim() === 'One-time booking')!;
    expect(oneTimeLink.getAttribute('href')).toBe('/bookings/new?resourceId=resource-1');
    const recurringTab = [...root.querySelectorAll('a')].find((a) => a.textContent?.trim() === 'Recurring booking')!;
    expect(recurringTab.classList.contains('booking-mode-tabs__tab--active')).toBe(true);
  });

  // Same DST gap as booking-form.spec.ts: America/New_York springs forward on 2027-03-14, so 2:30 AM
  // never happens that day - the series' first occurrence must be rejected inline, not silently shifted.
  it("rejects a series whose first occurrence's start time does not exist because of a spring-forward change", () => {
    setup();
    flushResource('America/New_York');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#start-date')!, '2027-03-14');
    setInputValue(root.querySelector('#r-start-time')!, '02:30');
    setInputValue(root.querySelector('#r-end-time')!, '04:00');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpTesting.expectNone((r) => r.url === SERIES_URL);
    expect(root.textContent).toContain('daylight-saving time change');
  });

  // 1:30 AM on the fall-back day (2027-11-07) happens twice - both are real, so the series must submit
  // normally rather than being rejected the way a nonexistent spring-forward time is.
  it('accepts an ambiguous fall-back first-occurrence time and submits normally', () => {
    setup();
    flushResource('America/New_York');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    setInputValue(root.querySelector('#start-date')!, '2027-11-07');
    setInputValue(root.querySelector('#r-start-time')!, '01:30');
    setInputValue(root.querySelector('#r-end-time')!, '03:00');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpTesting.expectOne((r) => r.url === SERIES_URL);
    expect(req.request.body.startDate).toBe('2027-11-07');
    expect(req.request.body.startTime).toBe('01:30');
    req.flush({
      seriesId: 'series-1',
      resourceId: 'resource-1',
      requestedOccurrenceCount: 10,
      createdOccurrences: [],
      conflicts: [],
    });
    fixture.detectChanges();

    expect(root.textContent).toContain('Recurring booking created');
  });
});

// A separate top-level describe (not nested above) because the viewer's zone is captured once, at
// component construction (`viewerZoneId = detectViewerTimeZone()`) - the Intl mock must be in place
// BEFORE TestBed.createComponent runs, which the shared setup() above already does unconditionally.
describe('RecurringBookingFormComponent - dual timezone display', () => {
  let fixture: ComponentFixture<RecurringBookingFormComponent>;
  let httpTesting: HttpTestingController;

  function setup(): void {
    TestBed.configureTestingModule({
      imports: [RecurringBookingFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ resourceId: 'resource-1' }) } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RecurringBookingFormComponent);
  }

  function flushResource(timeZoneId: string): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId,
    });
  }

  function submitAndFlush(timeZoneId: string): void {
    const root = fixture.nativeElement as HTMLElement;
    // A future date - the component rejects any start date before "today" on the real system clock.
    setInputValue(root.querySelector('#start-date')!, '2027-07-15');
    setInputValue(root.querySelector('#r-start-time')!, '10:15');
    setInputValue(root.querySelector('#r-end-time')!, '11:15');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    httpTesting.expectOne((r) => r.url === SERIES_URL).flush({
      seriesId: 'series-1',
      resourceId: 'resource-1',
      requestedOccurrenceCount: 1,
      createdOccurrences: [{ id: 'occurrence-1', startUtc: '2027-07-15T08:15:00Z', endUtc: '2027-07-15T09:15:00Z', status: 1 }],
      conflicts: [],
      timeZoneId,
    });
    fixture.detectChanges();
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it("shows each created occurrence's resource-local time as primary, and the viewer's local time as a secondary line when the zones differ", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    setup();
    flushResource('Europe/Sarajevo');
    fixture.detectChanges();
    submitAndFlush('Europe/Sarajevo');

    const root = fixture.nativeElement as HTMLElement;
    const occurrence = root.querySelector('.recurring-form__occurrence-time')!;
    expect(occurrence.textContent).toContain('10:15');
    expect(occurrence.textContent).toContain('Europe/Sarajevo');
    expect(occurrence.textContent).toContain('Your local time');
    expect(occurrence.textContent).toContain('17:15');
    expect(occurrence.textContent).toContain('Asia/Tokyo');
  });

  it('shows only one time per occurrence when the resource zone matches the viewer zone', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
    setup();
    flushResource('Europe/Sarajevo');
    fixture.detectChanges();
    submitAndFlush('Europe/Sarajevo');

    const root = fixture.nativeElement as HTMLElement;
    const occurrence = root.querySelector('.recurring-form__occurrence-time')!;
    expect(occurrence.textContent).toContain('10:15');
    expect(occurrence.textContent).not.toContain('Your local time');
  });
});
