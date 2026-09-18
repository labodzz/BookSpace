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
