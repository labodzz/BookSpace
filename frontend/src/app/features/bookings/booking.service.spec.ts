import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { BookingService } from './booking.service';
import { OwnBooking } from './booking.models';
import { OwnBookingWire } from './booking.mappers';

// The API serializes BookingStatus as its raw numeric code (no JsonStringEnumConverter) - 1 is
// Confirmed. These mock bodies represent the wire shape flush() sends back, not the mapped OwnBooking
// shape the service exposes afterwards.
function bookingsPage(count: number, startIndex: number): OwnBookingWire[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `booking-${startIndex + i}`,
    resourceId: 'resource-1',
    startUtc: '2026-09-02T10:00:00Z',
    endUtc: '2026-09-02T11:00:00Z',
    quantity: 1,
    status: 1,
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
  }));
}

const BOOKINGS_URL = `${environment.apiUrl}/bookings`;

describe('BookingService.getOwnBookingsInRange', () => {
  let service: BookingService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(BookingService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('makes a single request when the first page already covers every result', () => {
    let result: OwnBooking[] | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('100');
    req.flush({ items: bookingsPage(12, 0), page: 1, pageSize: 100, totalCount: 12 } satisfies PagedResult<OwnBookingWire>);

    expect(result).toHaveLength(12);
  });

  it('follows additional pages until every result has been fetched', () => {
    let result: OwnBooking[] | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '1')
      .flush({ items: bookingsPage(100, 0), page: 1, pageSize: 100, totalCount: 150 });
    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '2')
      .flush({ items: bookingsPage(50, 100), page: 2, pageSize: 100, totalCount: 150 });

    expect(result).toHaveLength(150);
  });

  // The hard requirement: even a pathological account can never turn one calendar range request into
  // an unbounded chain of API calls - it stops at MAX_RANGE_PAGES (5 pages of 100 = 500 bookings) even
  // though the server is still reporting more results are available.
  it('stops following pages at the safety cap even when more results remain', () => {
    let result: OwnBooking[] | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    for (let page = 1; page <= 5; page++) {
      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === String(page))
        .flush({ items: bookingsPage(100, (page - 1) * 100), page, pageSize: 100, totalCount: 1000 });
    }

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL && r.params.get('page') === '6');
    expect(result).toHaveLength(500);
  });
});
