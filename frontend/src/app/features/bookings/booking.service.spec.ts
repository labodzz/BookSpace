import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { BookingService } from './booking.service';
import { RangeBookingsResult } from './booking.models';
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

  it('makes a single request when there are fewer results than one page', () => {
    let result: RangeBookingsResult | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    const req = httpTesting.expectOne((r) => r.url === BOOKINGS_URL);
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('100');
    req.flush({ items: bookingsPage(12, 0), page: 1, pageSize: 100, totalCount: 12 } satisfies PagedResult<OwnBookingWire>);

    expect(result?.items).toHaveLength(12);
    expect(result?.truncated).toBe(false);
  });

  it('makes a single request when the first page is exactly full and there is nothing more', () => {
    let result: RangeBookingsResult | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL)
      .flush({ items: bookingsPage(100, 0), page: 1, pageSize: 100, totalCount: 100 });

    expect(result?.items).toHaveLength(100);
    expect(result?.truncated).toBe(false);
  });

  it('follows additional pages until every result has been fetched', () => {
    let result: RangeBookingsResult | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '1')
      .flush({ items: bookingsPage(100, 0), page: 1, pageSize: 100, totalCount: 150 });
    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '2')
      .flush({ items: bookingsPage(50, 100), page: 2, pageSize: 100, totalCount: 150 });

    expect(result?.items).toHaveLength(150);
    expect(result?.truncated).toBe(false);
  });

  // The hard requirement: even a pathological account can never turn one calendar range request into
  // an unbounded chain of API calls - it stops at MAX_RANGE_PAGES (50 pages of 100 = 5,000 bookings)
  // even though the server is still reporting more results are available. Unlike the old behavior, the
  // caller is told via `truncated` rather than getting back a range that silently looks complete.
  it('stops following pages at the safety cap and reports truncation when more results remain', () => {
    let result: RangeBookingsResult | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    for (let page = 1; page <= 50; page++) {
      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === String(page))
        .flush({ items: bookingsPage(100, (page - 1) * 100), page, pageSize: 100, totalCount: 10_000 });
    }

    httpTesting.expectNone((r) => r.url === BOOKINGS_URL && r.params.get('page') === '51');
    expect(result?.items).toHaveLength(5000);
    expect(result?.truncated).toBe(true);
  });

  it('does not report truncation when the cap is reached exactly as the last result is fetched', () => {
    let result: RangeBookingsResult | undefined;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe((r) => (result = r));

    for (let page = 1; page <= 50; page++) {
      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === String(page))
        .flush({ items: bookingsPage(100, (page - 1) * 100), page, pageSize: 100, totalCount: 5000 });
    }

    expect(result?.items).toHaveLength(5000);
    expect(result?.truncated).toBe(false);
  });

  // A failure partway through pagination must propagate as an error, not silently resolve with
  // whatever partial data happened to arrive before the failing page.
  it('propagates an error from a later page instead of returning partial results', () => {
    let error: unknown;
    service.getOwnBookingsInRange('2026-09-01T00:00:00Z', '2026-09-08T00:00:00Z').subscribe({ error: (e) => (error = e) });

    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '1')
      .flush({ items: bookingsPage(100, 0), page: 1, pageSize: 100, totalCount: 150 });
    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === '2')
      .flush('server error', { status: 500, statusText: 'Internal Server Error' });

    expect(error).toBeTruthy();
  });
});
