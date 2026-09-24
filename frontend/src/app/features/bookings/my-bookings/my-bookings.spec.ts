import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { MyBookingsComponent } from './my-bookings';

const BOOKINGS_URL = `${environment.apiUrl}/bookings`;
const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;

interface WireBookingOverrides {
  id: string;
  startUtc?: string;
  endUtc?: string;
  quantity?: number;
  status?: number;
  cancelledAtUtc?: string | null;
  cancelledByAdmin?: boolean;
  cancellationReason?: string | null;
  seriesId?: string | null;
  timeZoneId?: string;
}

function wireBooking(overrides: WireBookingOverrides) {
  return {
    resourceId: 'resource-1',
    startUtc: '2026-12-20T10:00:00Z',
    endUtc: '2026-12-20T11:00:00Z',
    quantity: 1,
    status: 1, // BookingStatus.Confirmed
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
    timeZoneId: 'UTC',
    ...overrides,
  };
}

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

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushMainList(items: ReturnType<typeof wireBooking>[], totalCount = items.length): void {
    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET' && !r.params.get('seriesId'))
      .flush({ items, page: 1, pageSize: 10, totalCount });
  }

  function flushResource(): void {
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
  }

  // The eager per-series fetch loadSeriesOccurrences fires immediately after the main page loads, for
  // every distinct seriesId present on it - every test involving a recurring booking must flush this too,
  // or httpTesting.verify() fails in afterEach.
  function flushSeriesOccurrences(seriesId: string, items: ReturnType<typeof wireBooking>[]): void {
    httpTesting
      .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('seriesId') === seriesId)
      .flush({ items, page: 1, pageSize: 100, totalCount: items.length });
  }

  function flushInitialLoad(): void {
    flushMainList([wireBooking({ id: 'booking-1' })]);
    flushResource();
  }

  it('renders a confirmed booking with its resolved resource name and a Cancel action', () => {
    flushInitialLoad();
    fixture.detectChanges();

    expect(root().textContent).toContain('Falcon Room');
    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Cancel')).toBe(true);
  });

  it('cancels a booking through the confirmation dialog, calls the API correctly, and refreshes the list', () => {
    flushInitialLoad();
    fixture.detectChanges();

    const cancelButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Cancel') as HTMLButtonElement;
    cancelButton.click();
    fixture.detectChanges();

    const confirmButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Cancel booking')) as HTMLButtonElement;
    confirmButton.click();

    const deleteReq = httpTesting.expectOne((r) => r.url === `${BOOKINGS_URL}/booking-1` && r.method === 'DELETE');
    expect(deleteReq.request.params.get('cancelRemainingSeries')).toBe('false');
    deleteReq.flush({
      id: 'booking-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00Z',
      endUtc: '2026-12-20T11:00:00Z',
      quantity: 1,
      status: 3, // BookingStatus.Cancelled
      cascadedOccurrenceIds: [],
    });

    // A successful cancel closes the dialog and reloads the list from the API rather than just
    // splicing the row out client-side.
    flushMainList([], 0);
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain("You don't have any bookings yet");
  });

  // Ordering is entirely the backend's responsibility (GetOwnBookingsAsync orders by CreatedAtUtc
  // descending) - this proves the component renders one-off bookings in exactly the order the server
  // returned them, without re-sorting by startUtc client-side and undoing that.
  it('renders one-off bookings in the exact order the server returned, not re-sorted by start time', () => {
    flushMainList([
      wireBooking({ id: 'booking-newer-request-earlier-start', startUtc: '2026-12-10T10:00:00Z', endUtc: '2026-12-10T11:00:00Z' }),
      wireBooking({ id: 'booking-older-request-later-start', startUtc: '2026-12-25T10:00:00Z', endUtc: '2026-12-25T11:00:00Z' }),
    ]);
    flushResource();
    fixture.detectChanges();

    const rowTimes = [...root().querySelectorAll('.booking-row__time')].map((el) => el.textContent);
    // The newer REQUEST (booking-newer-request-earlier-start, Dec 10) must render first even though it
    // STARTS before the older request (booking-older-request-later-start, Dec 25) - a client-side sort
    // by start time would reverse this order.
    expect(rowTimes[0]).toContain('Dec 10');
    expect(rowTimes[1]).toContain('Dec 25');
  });

  it('does not offer a Cancel action for a booking that is no longer cancellable', () => {
    flushMainList([wireBooking({ id: 'booking-2', status: 4 })]); // BookingStatus.Completed
    flushResource();
    fixture.detectChanges();

    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Cancel')).toBe(false);
  });

  it('collapses every occurrence of one recurring series into a single group row, not one row each', () => {
    const occurrences = [
      wireBooking({ id: 'occ-1', seriesId: 'series-1', startUtc: '2026-12-01T10:00:00Z', endUtc: '2026-12-01T11:00:00Z' }),
      wireBooking({ id: 'occ-2', seriesId: 'series-1', startUtc: '2026-12-08T10:00:00Z', endUtc: '2026-12-08T11:00:00Z' }),
      wireBooking({ id: 'occ-3', seriesId: 'series-1', startUtc: '2026-12-15T10:00:00Z', endUtc: '2026-12-15T11:00:00Z' }),
    ];
    flushMainList(occurrences);
    flushResource();
    flushSeriesOccurrences('series-1', occurrences);
    fixture.detectChanges();

    expect(root().querySelectorAll('.booking-row-series').length).toBe(1);
    expect(root().querySelectorAll('.booking-row').length).toBe(0);
    expect(root().textContent).toContain('3 bookings');
  });

  it('leaves a one-off booking and a recurring series both visible when the list mixes both', () => {
    const oneOff = wireBooking({ id: 'booking-1', startUtc: '2026-12-05T10:00:00Z', endUtc: '2026-12-05T11:00:00Z' });
    const occurrences = [
      wireBooking({ id: 'occ-1', seriesId: 'series-1', startUtc: '2026-12-01T10:00:00Z', endUtc: '2026-12-01T11:00:00Z' }),
      wireBooking({ id: 'occ-2', seriesId: 'series-1', startUtc: '2026-12-08T10:00:00Z', endUtc: '2026-12-08T11:00:00Z' }),
    ];
    flushMainList([oneOff, ...occurrences]);
    flushResource();
    flushSeriesOccurrences('series-1', occurrences);
    fixture.detectChanges();

    expect(root().querySelectorAll('.booking-row-series').length).toBe(1);
    // Only the one-off booking renders as a plain .booking-row outside any series group - the series'
    // own occurrences are nested inside its (currently collapsed) group and aren't in the DOM yet.
    const topLevelRows = [...root().querySelectorAll('.booking-row')].filter((el) => !el.closest('.booking-row-series'));
    expect(topLevelRows.length).toBe(1);
  });

  it('expanding a series group fetches and shows every occurrence of that series, and collapsing hides them again', () => {
    const occurrences = [
      wireBooking({ id: 'occ-1', seriesId: 'series-1', startUtc: '2026-12-01T10:00:00Z', endUtc: '2026-12-01T11:00:00Z' }),
      wireBooking({ id: 'occ-2', seriesId: 'series-1', startUtc: '2026-12-08T10:00:00Z', endUtc: '2026-12-08T11:00:00Z' }),
    ];
    flushMainList(occurrences);
    flushResource();
    flushSeriesOccurrences('series-1', occurrences);
    fixture.detectChanges();

    const toggle = root().querySelector('.booking-row-series__toggle') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(root().querySelectorAll('.booking-row-series__occurrences .booking-row').length).toBe(0);

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(root().querySelectorAll('.booking-row-series__occurrences .booking-row').length).toBe(2);

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(root().querySelectorAll('.booking-row-series__occurrences .booking-row').length).toBe(0);
  });

  // A series that has only one occurrence visible on THIS page must still show as an expandable group
  // (not a plain single row) - the series could have more occurrences on another page, and the group is
  // exactly what lets the user discover and see the rest of them.
  it('still groups a series with only one occurrence on this page, showing it as an expandable group', () => {
    const occurrences = [wireBooking({ id: 'occ-1', seriesId: 'series-1' })];
    flushMainList(occurrences);
    flushResource();
    flushSeriesOccurrences('series-1', occurrences);
    fixture.detectChanges();

    expect(root().querySelector('.booking-row-series__toggle')).not.toBeNull();
    const topLevelRows = [...root().querySelectorAll('.booking-row')].filter((el) => !el.closest('.booking-row-series'));
    expect(topLevelRows.length).toBe(0);
  });

  it('cancels an occurrence from inside an expanded series group and refreshes its occurrence list', () => {
    const occurrences = [
      wireBooking({ id: 'occ-1', seriesId: 'series-1', startUtc: '2026-12-01T10:00:00Z', endUtc: '2026-12-01T11:00:00Z' }),
      wireBooking({ id: 'occ-2', seriesId: 'series-1', startUtc: '2026-12-08T10:00:00Z', endUtc: '2026-12-08T11:00:00Z' }),
    ];
    flushMainList(occurrences);
    flushResource();
    flushSeriesOccurrences('series-1', occurrences);
    fixture.detectChanges();

    (root().querySelector('.booking-row-series__toggle') as HTMLButtonElement).click();
    fixture.detectChanges();

    const cancelButtons = [...root().querySelectorAll('.booking-row-series__occurrences button')].filter(
      (b) => b.textContent?.trim() === 'Cancel',
    );
    (cancelButtons[0] as HTMLButtonElement).click();
    fixture.detectChanges();

    const confirmButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Cancel booking')) as HTMLButtonElement;
    confirmButton.click();

    httpTesting.expectOne((r) => r.url === `${BOOKINGS_URL}/occ-1` && r.method === 'DELETE').flush({
      id: 'occ-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-01T10:00:00Z',
      endUtc: '2026-12-01T11:00:00Z',
      quantity: 1,
      status: 3, // BookingStatus.Cancelled
      cascadedOccurrenceIds: [],
    });

    const remaining = [wireBooking({ id: 'occ-2', seriesId: 'series-1', startUtc: '2026-12-08T10:00:00Z', endUtc: '2026-12-08T11:00:00Z' })];
    flushMainList(remaining);
    // The cancelled occurrence's seriesId cache entry is cleared on a successful cancel, so this reload
    // re-fetches the series' occurrences instead of serving the now-stale cached list.
    flushSeriesOccurrences('series-1', remaining);
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
  });
});

// Required scenario 5: My Bookings must no longer silently format a booking's time only through the
// browser's own timezone - it needs to show the resource's own local time as primary, with the viewer's
// local time as a secondary line only when the two actually differ (the dual-zone logic itself lives in
// BookingRowComponent, see booking-row/booking-row.ts, since <app-booking-row> is what actually renders
// each row now). A separate top-level describe (rather than nesting inside the block above) because the
// viewer's zone is captured once, at BookingRowComponent's own construction (`viewerZoneId =
// detectViewerTimeZone()`) - the Intl mock must be in place BEFORE TestBed.createComponent(MyBookingsComponent)
// runs (which transitively constructs every <app-booking-row> it renders), and the shared beforeEach
// above already calls that unconditionally.
describe('MyBookingsComponent - dual timezone display', () => {
  let fixture: ComponentFixture<MyBookingsComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [MyBookingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(MyBookingsComponent);
  }

  function flushBooking(timeZoneId: string): void {
    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-1', resourceId: 'resource-1', startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z',
          quantity: 1, status: 1, cancelledAtUtc: null, cancelledByAdmin: false, cancellationReason: null, seriesId: null,
          timeZoneId,
        },
      ],
      page: 1,
      pageSize: 10,
      totalCount: 1,
    });
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId,
    });
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it("shows the resource's own local time as primary, and the viewer's local time as a secondary line when the zones differ", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushBooking('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const primary = root.querySelector('.booking-row__time')!;
    const secondary = root.querySelector('.booking-row__time-secondary')!;
    // My Bookings always shows the date (alwaysShowDate: true, see my-bookings.ts's own comment) since
    // it's a flat list spanning many different days - so both times render in the full-date form here.
    expect(primary.textContent).toContain('Jul 15');
    expect(primary.textContent).toContain('10:15');
    expect(primary.textContent).toContain('11:15');
    expect(primary.textContent).toContain('Europe/Sarajevo');
    expect(secondary.textContent).toContain('17:15');
    expect(secondary.textContent).toContain('18:15');
    expect(secondary.textContent).toContain('Asia/Tokyo');
  });

  it('shows only one time when the booking timezone matches the viewer zone', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushBooking('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.booking-row__time-secondary')).toBeNull();
    const primary = root.querySelector('.booking-row__time')!;
    expect(primary.textContent).toContain('10:15');
    expect(primary.textContent).toContain('11:15');
  });
});
