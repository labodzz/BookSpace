import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DateTime } from 'luxon';
import { environment } from '../../../environments/environment';
import { OwnBookingWire } from '../bookings/booking.mappers';
import { CalendarComponent } from './calendar';

const BOOKINGS_URL = `${environment.apiUrl}/bookings`;
const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;

// A realistically busy single day for one resource - many recurring or overlapping-capacity bookings
// landing on the same calendar date, all referencing the same resource so only one resource lookup is
// needed to resolve every event's display name.
function busyDayBookings(count: number): OwnBookingWire[] {
  const today = DateTime.now().startOf('day');
  return Array.from({ length: count }, (_, i) => ({
    id: `booking-${i}`,
    resourceId: 'resource-1',
    startUtc: today.plus({ hours: i % 20 }).toUTC().toISO()!,
    endUtc: today.plus({ hours: (i % 20) + 1 }).toUTC().toISO()!,
    quantity: 1,
    status: 1,
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
    timeZoneId: 'UTC',
  }));
}

describe('CalendarComponent - bounded rendering on a busy day', () => {
  let fixture: ComponentFixture<CalendarComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CalendarComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CalendarComponent);
  });

  afterEach(() => httpTesting.verify());

  it('renders only the per-cell event cap in month view even with 150 bookings on one day, not one DOM node per booking', () => {
    fixture.detectChanges();

    const total = 150;
    for (let page = 1; page <= Math.ceil(total / 100); page++) {
      const remaining = total - (page - 1) * 100;
      const pageItems = busyDayBookings(Math.min(100, remaining)).map((b, i) => ({ ...b, id: `booking-${(page - 1) * 100 + i}` }));
      httpTesting
        .expectOne((r) => r.url === BOOKINGS_URL && r.params.get('page') === String(page))
        .flush({ items: pageItems, page, pageSize: 100, totalCount: total });
    }
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 200,
      requiresApproval: false,
      status: 0,
      timeZoneId: 'UTC',
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    // Every one of the 150 bookings landed on today's cell (busyDayBookings spreads them across only
    // 20 distinct hours) - month view must still cap that single cell's rendered events, not create 150
    // DOM buttons for it.
    const eventButtons = root.querySelectorAll('.calendar-event');
    expect(eventButtons.length).toBeLessThanOrEqual(3 * 42); // at most monthCellEventCap per cell, across the whole 6-week grid
    expect(root.textContent).toContain('more');

    const todayCell = root.querySelector('.calendar-cell--today')!;
    expect(todayCell.querySelectorAll('.calendar-event').length).toBe(3);
  });
});

// Required scenario 6: the grid must position every event using ONE consistent zone (the viewer's),
// never mixing in each event's own resource zone, while the event-detail dialog shows the resource's
// local time as a secondary line. A separate top-level describe (not nested above) because the viewer's
// zone is captured once, at component construction (`viewerZoneId = detectViewerTimeZone()`) - the Intl
// mock must be in place BEFORE TestBed.createComponent runs, which the shared beforeEach above already
// does unconditionally.
describe('CalendarComponent - dual timezone display', () => {
  let fixture: ComponentFixture<CalendarComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [CalendarComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CalendarComponent);
  }

  // Bracket access (bypassing `protected`) plus a loosened setter type, exactly like other specs in this
  // codebase reach into a component's own signals - sidesteps Luxon's DateTime<true>/<false> branded-type
  // friction for a literal this test already knows is a valid date.
  function setAnchor(iso: string): void {
    (fixture.componentInstance['anchor'] as unknown as { set(value: DateTime): void }).set(DateTime.fromISO(iso));
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  // Loads the exact same booking instant through a fresh CalendarComponent instance, with the resource
  // reporting the given timeZoneId, and returns which calendar day it landed on. Deliberately does NOT
  // mock Intl here (unlike the dialog test below): the grid's own bucketing goes through
  // toLocalDateTime()'s Luxon `.toLocal()`, which resolves Luxon's own cached system-zone singleton, not
  // a fresh Intl.DateTimeFormat().resolvedOptions() read each time - mocking Intl after Luxon has already
  // cached that singleton (from any earlier test in this same worker) would silently do nothing. Proving
  // "the grid ignores the resource's zone" therefore has to hold the real (unmocked) viewer/system zone
  // FIXED and vary only the resource zone, rather than trying to fake the viewer zone.
  function dayForBookingWithResourceZone(timeZoneId: string): string {
    TestBed.resetTestingModule();
    configure();
    setAnchor('2026-01-14');
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-1', resourceId: 'resource-1', startUtc: '2026-01-14T20:00:00Z', endUtc: '2026-01-14T21:00:00Z',
          quantity: 1, status: 1, cancelledAtUtc: null, cancelledByAdmin: false, cancellationReason: null, seriesId: null,
          timeZoneId,
        },
      ],
      page: 1,
      pageSize: 100,
      totalCount: 1,
    });
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId,
    });
    fixture.detectChanges();
    httpTesting.verify();

    const cells = fixture.componentInstance['cells']() as { date: DateTime; bookings: { id: string }[] }[];
    return cells.find((cell) => cell.bookings.some((booking) => booking.id === 'booking-1'))!.date.toISODate()!;
  }

  it("buckets an event on the same calendar day regardless of the resource's own timezone (one consistent viewer zone for the whole grid)", () => {
    const dayWithLosAngelesResource = dayForBookingWithResourceZone('America/Los_Angeles');
    const dayWithTokyoResource = dayForBookingWithResourceZone('Asia/Tokyo');

    // Same instant, two wildly different (16-hour-apart) resource zones - if the grid ever positioned by
    // resource zone instead of viewer zone, these would land on different calendar days.
    expect(dayWithTokyoResource).toBe(dayWithLosAngelesResource);
  });

  it("shows the viewer-local time as primary and the resource's local time as a secondary line in the event-detail dialog", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    setAnchor('2026-07-15');
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === BOOKINGS_URL && r.method === 'GET').flush({
      items: [
        {
          id: 'booking-1', resourceId: 'resource-1', startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z',
          quantity: 1, status: 1, cancelledAtUtc: null, cancelledByAdmin: false, cancellationReason: null, seriesId: null,
          timeZoneId: 'Europe/Sarajevo',
        },
      ],
      page: 1,
      pageSize: 100,
      totalCount: 1,
    });
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId: 'Europe/Sarajevo',
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('.calendar-event')!.click();
    fixture.detectChanges();

    expect(root.querySelector('.dialog__summary')!.textContent).toContain('17:15'); // viewer (Tokyo) primary
    expect(root.textContent).toContain("Resource's local time");
    expect(root.textContent).toContain('10:15'); // resource (Sarajevo) secondary
    expect(root.textContent).toContain('Europe/Sarajevo');
  });
});
