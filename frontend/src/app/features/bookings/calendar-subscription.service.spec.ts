import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { CalendarSubscriptionService } from './calendar-subscription.service';

const SUBSCRIPTIONS_URL = `${environment.apiUrl}/calendar-subscriptions`;

describe('CalendarSubscriptionService', () => {
  let service: CalendarSubscriptionService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CalendarSubscriptionService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('getStatus GETs /calendar-subscriptions', () => {
    let result: unknown;
    service.getStatus().subscribe((value) => (result = value));

    httpTesting.expectOne({ url: SUBSCRIPTIONS_URL, method: 'GET' }).flush({ hasActiveSubscription: true, createdAtUtc: '2026-01-01T00:00:00Z' });

    expect(result).toEqual({ hasActiveSubscription: true, createdAtUtc: '2026-01-01T00:00:00Z' });
  });

  it('create POSTs to /calendar-subscriptions with no body', () => {
    service.create().subscribe();

    const req = httpTesting.expectOne({ url: SUBSCRIPTIONS_URL, method: 'POST' });
    expect(req.request.body).toBeNull();
    req.flush({ feedUrl: 'https://bookspace.example.com/api/calendar-feed?token=abc', createdAtUtc: '2026-01-01T00:00:00Z' });
  });

  it('regenerate POSTs to /calendar-subscriptions/regenerate', () => {
    service.regenerate().subscribe();

    const req = httpTesting.expectOne({ url: `${SUBSCRIPTIONS_URL}/regenerate`, method: 'POST' });
    req.flush({ feedUrl: 'https://bookspace.example.com/api/calendar-feed?token=def', createdAtUtc: '2026-01-01T00:00:00Z' });
  });

  it('revoke DELETEs /calendar-subscriptions', () => {
    service.revoke().subscribe();

    httpTesting.expectOne({ url: SUBSCRIPTIONS_URL, method: 'DELETE' }).flush(null);
  });
});
