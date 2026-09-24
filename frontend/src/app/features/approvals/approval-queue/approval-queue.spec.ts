import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../../environments/environment';
import { ApprovalQueueComponent } from './approval-queue';

const PENDING_URL = `${environment.apiUrl}/bookings/pending-approval`;
const RESOURCE_URL = `${environment.apiUrl}/resources/resource-1`;
const APPROVE_URL_PREFIX = `${environment.apiUrl}/bookings/`;

describe('ApprovalQueueComponent', () => {
  let fixture: ComponentFixture<ApprovalQueueComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ApprovalQueueComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ApprovalQueueComponent);
  });

  afterEach(() => httpTesting.verify());

  function flushInitialLoad(): void {
    httpTesting.expectOne((r) => r.url === PENDING_URL).flush([
      {
        bookingId: 'booking-1',
        resourceId: 'resource-1',
        userId: 'user-1',
        startUtc: '2026-12-20T10:00:00Z',
        endUtc: '2026-12-20T11:00:00Z',
        quantity: 1,
        expiresAtUtc: '2026-12-21T10:00:00Z',
        seriesId: null,
        timeZoneId: 'UTC',
      },
    ]);
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: true,
      status: 0, // ResourceStatus.Active - raw numeric wire code
      timeZoneId: 'UTC',
    });
  }

  function flushSeriesLoad(): void {
    httpTesting.expectOne((r) => r.url === PENDING_URL).flush([
      {
        bookingId: 'occurrence-1',
        resourceId: 'resource-1',
        userId: 'user-1',
        startUtc: '2026-12-20T10:00:00Z',
        endUtc: '2026-12-20T11:00:00Z',
        quantity: 1,
        expiresAtUtc: '2026-12-21T10:00:00Z',
        seriesId: 'series-1',
        timeZoneId: 'UTC',
      },
      {
        bookingId: 'occurrence-2',
        resourceId: 'resource-1',
        userId: 'user-1',
        startUtc: '2026-12-27T10:00:00Z',
        endUtc: '2026-12-27T11:00:00Z',
        quantity: 1,
        expiresAtUtc: '2026-12-28T10:00:00Z',
        seriesId: 'series-1',
        timeZoneId: 'UTC',
      },
    ]);
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: true,
      status: 0,
      timeZoneId: 'UTC',
    });
  }

  it('renders a pending request with its resolved resource name', () => {
    flushInitialLoad();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Falcon Room');
  });

  it('approves a request and refreshes the queue from the API afterwards', () => {
    flushInitialLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const approveButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Approve') as HTMLButtonElement;
    approveButton.click();

    const req = httpTesting.expectOne((r) => r.url === `${APPROVE_URL_PREFIX}booking-1/approve`);
    expect(req.request.body).toEqual({ decisionNote: null, approveRemainingSeries: false });
    req.flush({
      id: 'booking-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00Z',
      endUtc: '2026-12-20T11:00:00Z',
      quantity: 1,
      status: 1, // BookingStatus.Confirmed - raw numeric wire code
      cascadedApprovedOccurrenceIds: [],
      cascadedConflicts: [],
    });

    httpTesting.expectOne((r) => r.url === PENDING_URL).flush([]);
    fixture.detectChanges();

    expect(root.textContent).toContain('Nothing waiting on you');
  });

  it('shows an inline row error when a decision fails, instead of a silent failure', () => {
    flushInitialLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const approveButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Approve') as HTMLButtonElement;
    approveButton.click();

    httpTesting
      .expectOne((r) => r.url === `${APPROVE_URL_PREFIX}booking-1/approve`)
      .flush({ title: 'Conflict', detail: 'This request is no longer pending.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain('This request is no longer pending.');
  });

  it('groups occurrences sharing a seriesId and offers a single "approve all" action', () => {
    flushSeriesLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Recurring series');
    expect(root.textContent).toContain('2 pending occurrences');
    expect([...root.querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Approve all 2')).toBe(true);
  });

  it('approves an entire series in one call and reports cascaded conflicts, without force-approving them', () => {
    flushSeriesLoad();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const approveAllButton = [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Approve all 2') as HTMLButtonElement;
    approveAllButton.click();

    const req = httpTesting.expectOne((r) => r.url === `${APPROVE_URL_PREFIX}occurrence-1/approve`);
    expect(req.request.body).toEqual({ decisionNote: null, approveRemainingSeries: true });
    req.flush({
      id: 'occurrence-1',
      resourceId: 'resource-1',
      startUtc: '2026-12-20T10:00:00Z',
      endUtc: '2026-12-20T11:00:00Z',
      quantity: 1,
      status: 1,
      cascadedApprovedOccurrenceIds: [],
      cascadedConflicts: [{ bookingId: 'occurrence-2', reason: 'Booking.BlackoutConflict' }],
    });

    // A conflicted sibling is never force-approved - the queue simply reloads from the API afterwards,
    // which would still list it as pending. resource-1's name is already cached from the first load, so
    // no second resource fetch is expected here.
    httpTesting.expectOne((r) => r.url === PENDING_URL).flush([
      {
        bookingId: 'occurrence-2',
        resourceId: 'resource-1',
        userId: 'user-1',
        startUtc: '2026-12-27T10:00:00Z',
        endUtc: '2026-12-27T11:00:00Z',
        quantity: 1,
        expiresAtUtc: '2026-12-28T10:00:00Z',
        seriesId: 'series-1',
        timeZoneId: 'UTC',
      },
    ]);
    fixture.detectChanges();

    // A lone remaining pending occurrence (no other sibling left pending) renders as a plain row, not a
    // series group of one.
    expect(root.textContent).not.toContain('Recurring series');
    expect([...root.querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Approve')).toBe(true);
  });
});

// A separate top-level describe (not nested above) because the viewer's zone is captured once, at
// component construction (`viewerZoneId = detectViewerTimeZone()`) - the Intl mock must be in place
// BEFORE TestBed.createComponent runs, which the shared beforeEach above already does unconditionally.
describe('ApprovalQueueComponent - dual timezone display', () => {
  let fixture: ComponentFixture<ApprovalQueueComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [ApprovalQueueComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ApprovalQueueComponent);
  }

  function flushApproval(timeZoneId: string): void {
    httpTesting.expectOne((r) => r.url === PENDING_URL).flush([
      {
        bookingId: 'booking-1', resourceId: 'resource-1', userId: 'user-1',
        startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z',
        quantity: 1, expiresAtUtc: '2026-12-21T10:00:00Z', seriesId: null, timeZoneId,
      },
    ]);
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush({
      id: 'resource-1', resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: true, status: 0, timeZoneId,
    });
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it("shows the resource's own local time as primary, and the approver's local time as a secondary line when the zones differ", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushApproval('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const primary = root.querySelector('.approval-row__time')!;
    const secondary = root.querySelector('.approval-row__time-secondary')!;
    expect(primary.textContent).toContain('10:15');
    expect(primary.textContent).toContain('Europe/Sarajevo');
    expect(secondary.textContent).toContain('17:15');
    expect(secondary.textContent).toContain('Asia/Tokyo');
  });

  it('shows only one time when the resource zone matches the approver zone', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushApproval('Europe/Sarajevo');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.approval-row__time-secondary')).toBeNull();
    expect(root.querySelector('.approval-row__time')!.textContent).toContain('10:15');
  });
});
