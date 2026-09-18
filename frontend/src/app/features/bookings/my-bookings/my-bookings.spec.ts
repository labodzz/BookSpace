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
