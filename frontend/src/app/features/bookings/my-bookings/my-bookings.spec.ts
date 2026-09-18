import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { MyBookingsComponent } from './my-bookings';

const BOOKINGS_URL = `${environment.apiUrl}/bookings`;
const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;

describe('MyBookingsComponent', () => {
  let fixture: ComponentFixture<MyBookingsComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MyBookingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(MyBookingsComponent);
  });

  afterEach(() => httpTesting.verify());

  function flushInitialLoad(): void {
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-1',
          resourceId: 'resource-1',
          startUtc: '2026-12-20T10:00:00Z',
          endUtc: '2026-12-20T11:00:00Z',
          quantity: 1,
          status: 1, // BookingStatus.Confirmed - raw numeric wire code
          cancelledAtUtc: null,
          cancelledByAdmin: false,
          cancellationReason: null,
          seriesId: null,
        },
      ],
      page: 1,
      pageSize: 10,
      totalCount: 1,
    });
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 0, // ResourceStatus.Active - raw numeric wire code
      timeZoneId: 'UTC',
    });
  }

  it('renders a confirmed booking with its resolved resource name and a Cancel action', () => {
    flushInitialLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Falcon Room');
    expect([...root.querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Cancel')).toBe(true);
  });

  it('cancels a booking through the confirmation dialog, calls the API correctly, and refreshes the list', () => {
    flushInitialLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const cancelButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Cancel') as HTMLButtonElement;
    cancelButton.click();
    fixture.detectChanges();

    const confirmButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.includes('Cancel booking')) as HTMLButtonElement;
    confirmButton.click();

    const deleteReq = httpTesting.expectOne((r) => r.url === `${BOOKINGS_URL}/booking-1` && r.method === 'DELETE');
    expect(deleteReq.request.params.get('cancelRemainingSeries')).toBe('false');
    deleteReq.flush({
      id: 'booking-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00Z',
      endUtc: '2026-12-20T11:00:00Z',
      quantity: 1,
      status: 3, // BookingStatus.Cancelled - raw numeric wire code
      cascadedOccurrenceIds: [],
    });

    // A successful cancel closes the dialog and reloads the list from the API rather than just
    // splicing the row out client-side.
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({ items: [], page: 1, pageSize: 10, totalCount: 0 });
    fixture.detectChanges();

    expect(root.querySelector('.dialog')).toBeNull();
    expect(root.textContent).toContain("You don't have any bookings yet");
  });

  // Ordering is entirely the backend's responsibility (GetOwnBookingsAsync orders by CreatedAtUtc
  // descending) - this proves the component renders `bookings()` in exactly the order the server
  // returned it, without re-sorting by startUtc client-side and undoing that.
  it('renders bookings in the exact order the server returned, not re-sorted by start time', () => {
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-newer-request-earlier-start',
          resourceId: 'resource-1',
          startUtc: '2026-12-10T10:00:00Z',
          endUtc: '2026-12-10T11:00:00Z',
          quantity: 1,
          status: 1,
          cancelledAtUtc: null,
          cancelledByAdmin: false,
          cancellationReason: null,
          seriesId: null,
        },
        {
          id: 'booking-older-request-later-start',
          resourceId: 'resource-1',
          startUtc: '2026-12-25T10:00:00Z',
          endUtc: '2026-12-25T11:00:00Z',
          quantity: 1,
          status: 1,
          cancelledAtUtc: null,
          cancelledByAdmin: false,
          cancellationReason: null,
          seriesId: null,
        },
      ],
      page: 1,
      pageSize: 10,
      totalCount: 2,
    });
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 0,
      timeZoneId: 'UTC',
    });
    fixture.detectChanges();

    const rowTimes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.booking-row__time')].map((el) => el.textContent);
    // The newer REQUEST (booking-newer-request-earlier-start, Dec 10) must render first even though it
    // STARTS before the older request (booking-older-request-later-start, Dec 25) - a client-side sort
    // by start time would reverse this order.
    expect(rowTimes[0]).toContain('Dec 10');
    expect(rowTimes[1]).toContain('Dec 25');
  });

  it('does not offer a Cancel action for a booking that is no longer cancellable', () => {
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-2',
          resourceId: 'resource-1',
          startUtc: '2026-12-20T10:00:00Z',
          endUtc: '2026-12-20T11:00:00Z',
          quantity: 1,
          status: 4, // BookingStatus.Completed - raw numeric wire code
          cancelledAtUtc: null,
          cancelledByAdmin: false,
          cancellationReason: null,
          seriesId: null,
        },
      ],
      page: 1,
      pageSize: 10,
      totalCount: 1,
    });
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 0, // ResourceStatus.Active - raw numeric wire code
      timeZoneId: 'UTC',
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect([...root.querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Cancel')).toBe(false);
  });
});
