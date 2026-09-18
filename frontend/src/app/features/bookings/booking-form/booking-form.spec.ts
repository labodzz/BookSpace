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
