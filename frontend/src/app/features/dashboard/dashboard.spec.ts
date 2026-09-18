import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';
import { DashboardComponent } from './dashboard';

describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: { hasAnyRole: () => false, displayName: () => 'Alex' } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DashboardComponent);
  });

  afterEach(() => httpTesting.verify());

  function flushEverythingExceptUpcoming(): void {
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/bookings` && r.params.get('pageSize') === '1').flush({ items: [], page: 1, pageSize: 1, totalCount: 0 });
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources`).flush({ items: [], page: 1, pageSize: 4, totalCount: 0 });
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resource-types`).flush([]);
  }

  // Mirrors the BookingService test: getOwnBookingsInRange's safety cap (50 pages of 100) hit while the
  // server still reports more results - the dashboard's "Upcoming bookings" stat must show this is a
  // lower bound (a "+" suffix), not present a truncated count as if it were exact.
  it('marks the upcoming-bookings count as a lower bound when the range fetch is truncated', () => {
    fixture.detectChanges();
    flushEverythingExceptUpcoming();

    for (let page = 1; page <= 50; page++) {
      httpTesting
        .expectOne((r) => r.url === `${environment.apiUrl}/bookings` && r.params.get('fromUtc') !== null && r.params.get('page') === String(page))
        .flush({
          items: Array.from({ length: 100 }, (_, i) => ({
            id: `booking-${(page - 1) * 100 + i}`,
            resourceId: 'resource-1',
            startUtc: '2026-09-02T10:00:00Z',
            endUtc: '2026-09-02T11:00:00Z',
            quantity: 1,
            status: 1,
            cancelledAtUtc: null,
            cancelledByAdmin: false,
            cancellationReason: null,
            seriesId: null,
          })),
          page,
          pageSize: 100,
          totalCount: 10_000,
        });
    }
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/resource-1`).flush({
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

    const statValue = (fixture.nativeElement as HTMLElement).querySelector('.stat-card__value')!.textContent;
    expect(statValue).toContain('+');
  });
});
