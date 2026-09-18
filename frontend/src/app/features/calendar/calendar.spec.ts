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
