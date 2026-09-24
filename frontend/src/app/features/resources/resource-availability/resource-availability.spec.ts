import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { DateTime } from 'luxon';
import { environment } from '../../../../environments/environment';
import { ResourceAvailability } from '../resource.models';
import { ResourceAvailabilityComponent } from './resource-availability';

const RESOURCE_ID = 'resource-1';
const RESOURCE_URL = `${environment.apiUrl}/resources/${RESOURCE_ID}`;

function setDateInput(fixture: ComponentFixture<ResourceAvailabilityComponent>, id: string, value: string): void {
  const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`#${id}`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function submitForm(fixture: ComponentFixture<ResourceAvailabilityComponent>): void {
  (fixture.nativeElement as HTMLElement).querySelector('form')!.dispatchEvent(new Event('submit'));
}

describe('ResourceAvailabilityComponent', () => {
  let fixture: ComponentFixture<ResourceAvailabilityComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ResourceAvailabilityComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => RESOURCE_ID } } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceAvailabilityComponent);

    httpTesting.expectOne(RESOURCE_URL).flush({
      id: RESOURCE_ID,
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 0,
      timeZoneId: 'UTC',
    });
    fixture.detectChanges();
    // The initial default-window load fired by loadResource() itself - drained here so each test starts
    // from a clean slate of pending requests.
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(emptyAvailability());
    fixture.detectChanges();
  });

  afterEach(() => httpTesting.verify());

  function emptyAvailability(overrides: Partial<ResourceAvailability> = {}): ResourceAvailability {
    return {
      resourceId: RESOURCE_ID,
      fromDate: '2026-01-01',
      toDate: '2026-01-01',
      timeZoneId: 'UTC',
      capacity: 4,
      openPeriods: [],
      blackouts: [],
      busyPeriods: [],
      bookableSlots: [],
      ...overrides,
    };
  }

  // The backend rejects a range once ToDate.DayNumber - FromDate.DayNumber reaches 92 (see
  // GetResourceAvailabilityQueryRequestValidator) - the frontend must reject at exactly the same
  // boundary, neither one day early nor one day late.
  describe('date range boundary validation (mirrors the backend: max ACCEPTED difference is 91 days)', () => {
    const from = '2026-01-01';

    it('accepts a range one day below the maximum (91 - 1 = 90 days difference)', () => {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', DateTime.fromISO(from).plus({ days: 90 }).toISODate()!);
      submitForm(fixture);

      expect(fixture.componentInstance['rangeError']()).toBeNull();
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(emptyAvailability());
    });

    it('accepts a range of exactly the maximum accepted difference (91 days)', () => {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', DateTime.fromISO(from).plus({ days: 91 }).toISODate()!);
      submitForm(fixture);

      expect(fixture.componentInstance['rangeError']()).toBeNull();
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(emptyAvailability());
    });

    it('rejects a range one day above the maximum (92 days difference) without calling the API', () => {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', DateTime.fromISO(from).plus({ days: 92 }).toISODate()!);
      submitForm(fixture);

      expect(fixture.componentInstance['rangeError']()).toContain("can't span more than 92 days");
      httpTesting.expectNone((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`);
    });

    it('accepts the same start and end date', () => {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', from);
      submitForm(fixture);

      expect(fixture.componentInstance['rangeError']()).toBeNull();
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(emptyAvailability());
    });

    it('rejects a reversed range (end before start) without calling the API', () => {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', DateTime.fromISO(from).minus({ days: 1 }).toISODate()!);
      submitForm(fixture);

      expect(fixture.componentInstance['rangeError']()).toContain('on or after');
      httpTesting.expectNone((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`);
    });
  });

  describe('multi-day blackout rendering', () => {
    function search(from: string, to: string): void {
      setDateInput(fixture, 'from', from);
      setDateInput(fixture, 'to', to);
      submitForm(fixture);
    }

    function daysWithBlackout(): (string | null)[] {
      return fixture.componentInstance['days']()
        .filter((day: { blackouts: unknown[] }) => day.blackouts.length > 0)
        .map((day: { date: DateTime }) => day.date.toISODate());
    }

    it('shows a blackout confined to a single day on only that day', () => {
      search('2026-01-01', '2026-01-05');
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(
        emptyAvailability({
          fromDate: '2026-01-01',
          toDate: '2026-01-05',
          blackouts: [{ startUtc: '2026-01-02T10:00:00Z', endUtc: '2026-01-02T14:00:00Z', reason: 'Maintenance' }],
        }),
      );
      fixture.detectChanges();

      expect(daysWithBlackout()).toEqual(['2026-01-02']);
    });

    it('shows a blackout spanning three days on every one of those days', () => {
      search('2026-01-01', '2026-01-05');
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(
        emptyAvailability({
          fromDate: '2026-01-01',
          toDate: '2026-01-05',
          blackouts: [{ startUtc: '2026-01-02T18:00:00Z', endUtc: '2026-01-04T09:00:00Z', reason: 'Renovation' }],
        }),
      );
      fixture.detectChanges();

      expect(daysWithBlackout()).toEqual(['2026-01-02', '2026-01-03', '2026-01-04']);
    });

    // endUtc is exclusive (matches the backend's half-open [Start, End) convention) - a blackout ending
    // exactly at a day's local midnight must occupy every day up to that boundary, but never the day
    // that starts at it.
    it('does not let a blackout ending exactly at local midnight bleed into the next day', () => {
      search('2026-01-01', '2026-01-05');
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(
        emptyAvailability({
          fromDate: '2026-01-01',
          toDate: '2026-01-05',
          blackouts: [{ startUtc: '2026-01-02T00:00:00Z', endUtc: '2026-01-03T00:00:00Z', reason: 'Full day' }],
        }),
      );
      fixture.detectChanges();

      expect(daysWithBlackout()).toEqual(['2026-01-02']);
    });

    it('clips a blackout that starts before the requested range to only the days actually in range', () => {
      search('2026-01-03', '2026-01-05');
      httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush(
        emptyAvailability({
          fromDate: '2026-01-03',
          toDate: '2026-01-05',
          blackouts: [{ startUtc: '2025-12-30T00:00:00Z', endUtc: '2026-01-04T00:00:00Z', reason: 'Long outage' }],
        }),
      );
      fixture.detectChanges();

      expect(daysWithBlackout()).toEqual(['2026-01-03']);
    });
  });
});

// A separate top-level describe (not nested in the block above) because the viewer's zone is captured
// once, at component construction (`viewerZoneId = detectViewerTimeZone()`) - the Intl mock must be in
// place BEFORE TestBed.createComponent runs, which the shared beforeEach above already does unconditionally.
describe('ResourceAvailabilityComponent - dual timezone display', () => {
  let fixture: ComponentFixture<ResourceAvailabilityComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [ResourceAvailabilityComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => RESOURCE_ID } } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceAvailabilityComponent);
  }

  function flushWithSlot(timeZoneId: string): void {
    httpTesting.expectOne(RESOURCE_URL).flush({
      id: RESOURCE_ID, resourceTypeId: 'type-1', name: 'Falcon Room', description: null,
      capacity: 4, requiresApproval: false, status: 0, timeZoneId,
    });
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}/availability`).flush({
      resourceId: RESOURCE_ID, fromDate: '2026-07-15', toDate: '2026-07-15', timeZoneId, capacity: 4,
      openPeriods: [{ startUtc: '2026-07-15T00:00:00Z', endUtc: '2026-07-16T00:00:00Z' }], blackouts: [], busyPeriods: [],
      bookableSlots: [{ startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z', availableCapacity: 4 }],
    });
    fixture.detectChanges();
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  it("shows the resource's own local time as primary, and the viewer's local time as a secondary line when the zones differ", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushWithSlot('Europe/Sarajevo');

    const root = fixture.nativeElement as HTMLElement;
    const slot = root.querySelector('.availability__slot')!;
    expect(slot.textContent).toContain('10:15–11:15');
    expect(slot.querySelector('.availability__slot-secondary')?.textContent).toContain('17:15–18:15');
  });

  it('shows only one time when the resource zone matches the viewer zone', () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
    configure();
    flushWithSlot('Europe/Sarajevo');

    const root = fixture.nativeElement as HTMLElement;
    const slot = root.querySelector('.availability__slot')!;
    expect(slot.textContent).toContain('10:15–11:15');
    expect(slot.querySelector('.availability__slot-secondary')).toBeNull();
  });
});
