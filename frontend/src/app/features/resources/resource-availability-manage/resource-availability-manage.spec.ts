import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceAvailabilityManageComponent } from './resource-availability-manage';

const RESOURCE_ID = 'resource-1';
const RESOURCE_URL = `${environment.apiUrl}/resources/${RESOURCE_ID}`;
const RULES_URL = `${RESOURCE_URL}/availability-rules`;
const BLACKOUTS_URL = `${RESOURCE_URL}/blackout-periods`;

function wireResource(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: RESOURCE_ID,
    resourceTypeId: 'type-1',
    name: 'Falcon Room',
    description: null,
    capacity: 4,
    requiresApproval: false,
    status: 0, // ResourceStatus.Active
    timeZoneId: 'America/New_York',
    ...overrides,
  };
}

describe('ResourceAvailabilityManageComponent', () => {
  let fixture: ComponentFixture<ResourceAvailabilityManageComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [ResourceAvailabilityManageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => RESOURCE_ID } } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceAvailabilityManageComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushAll(resourceOverrides: Partial<Record<string, unknown>> = {}, rules: unknown[] = [], blackouts: unknown[] = []): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL && r.method === 'GET').flush(wireResource(resourceOverrides));
    httpTesting.expectOne((r) => r.url === RULES_URL && r.method === 'GET').flush(rules);
    httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'GET').flush(blackouts);
    fixture.detectChanges();
  }

  function setValue(selector: string, value: string, eventType: 'input' | 'change' = 'input'): void {
    const el = root().querySelector(selector) as HTMLInputElement | HTMLSelectElement;
    el.value = value;
    el.dispatchEvent(new Event(eventType));
  }

  function buttonWithText(text: string): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  // Once a dialog is open, its own action buttons (e.g. "Delete") can share a label with a button in
  // the underlying list row - this scopes the query to just the open dialog so the right one is clicked.
  function dialogButtonWithText(text: string): HTMLButtonElement {
    const dialog = root().querySelector('.dialog') as HTMLElement;
    return [...dialog.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  afterEach(() => httpTesting.verify());

  it('shows a loading skeleton before the resource loads', () => {
    configure();
    fixture.detectChanges();

    expect(root().querySelector('.manage-availability--skeleton')).not.toBeNull();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource());
    httpTesting.expectOne((r) => r.url === RULES_URL).flush([]);
    httpTesting.expectOne((r) => r.url === BLACKOUTS_URL).flush([]);
  });

  it('loads and displays rules sorted by day (Monday-first) then start time, with the resource timezone shown', () => {
    configure();
    flushAll(
      {},
      [
        { id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 0, startTime: '10:00:00', endTime: '12:00:00' }, // Sunday
        { id: 'r2', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '13:00:00', endTime: '17:00:00' }, // Monday afternoon
        { id: 'r3', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '09:00:00', endTime: '12:00:00' }, // Monday morning
      ],
    );

    const rows = [...root().querySelectorAll('.manage-availability__rule')].map((el) => el.textContent);
    expect(rows[0]).toContain('Monday');
    expect(rows[0]).toContain('09:00');
    expect(rows[1]).toContain('Monday');
    expect(rows[1]).toContain('13:00');
    expect(rows[2]).toContain('Sunday');
    expect(root().textContent).toContain('America/New_York');
  });

  it('shows an empty-state explanation when there are no rules yet', () => {
    configure();
    flushAll();

    expect(root().textContent).toContain('No weekly availability configured yet');
  });

  it('creates a valid rule, appends it to the list, and shows a success toast', () => {
    configure();
    flushAll();

    setValue('#rule-day', 'Wednesday', 'change');
    setValue('#rule-start', '08:00');
    setValue('#rule-end', '10:00');
    buttonWithText('Add rule').click();

    const req = httpTesting.expectOne((r) => r.url === RULES_URL && r.method === 'POST');
    expect(req.request.body).toEqual({ dayOfWeek: 3, startTime: '08:00:00', endTime: '10:00:00' });
    req.flush({ id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 3, startTime: '08:00:00', endTime: '10:00:00' });
    fixture.detectChanges();

    expect(root().textContent).toContain('Wednesday');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Availability rule added.' })]);
  });

  it('rejects an end time that is not after the start time, without an HTTP call', () => {
    configure();
    flushAll();

    setValue('#rule-start', '10:00');
    setValue('#rule-end', '09:00');
    buttonWithText('Add rule').click();
    fixture.detectChanges();

    expect(root().textContent).toContain('End time must be after the start time.');
    httpTesting.expectNone((r) => r.url === RULES_URL && r.method === 'POST');
  });

  it('prevents a duplicate rule submission while the first request is still in flight', () => {
    configure();
    flushAll();

    setValue('#rule-start', '08:00');
    setValue('#rule-end', '10:00');
    buttonWithText('Add rule').click();
    buttonWithText('Add rule').click(); // second click before the first response arrives

    httpTesting.expectOne((r) => r.url === RULES_URL && r.method === 'POST').flush({
      id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '08:00:00', endTime: '10:00:00',
    });
  });

  it('surfaces an identical-rule conflict from the backend', () => {
    configure();
    flushAll();

    setValue('#rule-start', '08:00');
    setValue('#rule-end', '10:00');
    buttonWithText('Add rule').click();

    httpTesting
      .expectOne((r) => r.url === RULES_URL && r.method === 'POST')
      .flush(
        { title: 'Conflict', detail: 'An identical availability rule already exists for this resource.', errorCode: 'AvailabilityRule.Conflict' },
        { status: 409, statusText: 'Conflict' },
      );
    fixture.detectChanges();

    expect(root().textContent).toContain('An identical availability rule already exists');
  });

  it('deletes a rule after confirmation', () => {
    configure();
    flushAll({}, [{ id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();
    expect(root().querySelector('.dialog')).not.toBeNull();

    dialogButtonWithText('Delete').click();
    httpTesting.expectOne((r) => r.url === `${RULES_URL}/r1` && r.method === 'DELETE').flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain('No weekly availability configured yet');
  });

  it('keeps the rule visible if deletion fails', () => {
    configure();
    flushAll({}, [{ id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' }]);

    buttonWithText('Delete').click();
    fixture.detectChanges();
    dialogButtonWithText('Delete').click();

    httpTesting
      .expectOne((r) => r.url === `${RULES_URL}/r1` && r.method === 'DELETE')
      .flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().textContent).toContain('Monday');
  });

  describe('archived resource', () => {
    it('shows the archived notice and hides the add-rule form and add-blackout button, but still allows delete/edit', () => {
      configure();
      flushAll(
        { status: 3 }, // Archived
        [{ id: 'r1', resourceId: RESOURCE_ID, dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' }],
        [{ id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-01-01T09:00:00Z', endUtc: '2027-01-01T13:00:00Z', reason: 'Maintenance' }],
      );

      expect(root().textContent).toContain('This resource is archived');
      expect(root().querySelector('#rule-day')).toBeNull();
      expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Add blackout')).toBe(false);
      expect(buttonWithText('Delete')).not.toBeUndefined();
      expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Edit')).toBe(true);
    });
  });

  describe('blackout periods', () => {
    function openCreateDialog(): void {
      buttonWithText('Add blackout').click();
      fixture.detectChanges();
    }

    it('shows an empty-state explanation when there are no blackouts yet', () => {
      configure();
      flushAll();

      expect(root().textContent).toContain('No blackout periods configured');
    });

    it('creates a valid blackout in the resource timezone and converts it to UTC at the API boundary', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '13:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();

      // America/New_York is UTC-4 (EDT) on 2027-06-10.
      const req = httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
      expect(req.request.body).toEqual({ startUtc: '2027-06-10T13:00:00.000Z', endUtc: '2027-06-10T17:00:00.000Z', reason: 'Maintenance' });
      req.flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00.000Z', endUtc: '2027-06-10T17:00:00.000Z', reason: 'Maintenance',
        conflictingBookingIds: [],
      });
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).toBeNull();
      expect(root().textContent).toContain('Maintenance');
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Blackout period added.' })]);
    });

    it('displays an existing blackout converted back into the resource timezone, not the browser timezone', () => {
      configure();
      // 2027-06-10T13:00:00Z is 09:00 EDT (America/New_York, UTC-4) - never the bare UTC hour.
      flushAll({}, [], [{ id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00Z', endUtc: '2027-06-10T17:00:00Z', reason: 'Maintenance' }]);

      expect(root().textContent).toContain('10 June 2027, 09:00 to 10 June 2027, 13:00');
    });

    it('prefills the edit dialog with the blackout converted back to resource-local date/time', () => {
      configure();
      flushAll({}, [], [{ id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00Z', endUtc: '2027-06-10T17:00:00Z', reason: 'Maintenance' }]);

      buttonWithText('Edit').click();
      fixture.detectChanges();

      expect((root().querySelector('#blackout-start-date') as HTMLInputElement).value).toBe('2027-06-10');
      expect((root().querySelector('#blackout-start-time') as HTMLInputElement).value).toBe('09:00');
      expect((root().querySelector('#blackout-reason') as HTMLInputElement).value).toBe('Maintenance');
    });

    it('updates a blackout and reports no conflicts when none exist', () => {
      configure();
      flushAll({}, [], [{ id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00Z', endUtc: '2027-06-10T17:00:00Z', reason: 'Maintenance' }]);

      buttonWithText('Edit').click();
      fixture.detectChanges();
      setValue('#blackout-reason', 'Rescheduled maintenance');
      buttonWithText('Save changes').click();

      const req = httpTesting.expectOne((r) => r.url === `${BLACKOUTS_URL}/b1` && r.method === 'PUT');
      expect(req.request.body.reason).toBe('Rescheduled maintenance');
      req.flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00Z', endUtc: '2027-06-10T17:00:00Z', reason: 'Rescheduled maintenance',
        conflictingBookingIds: [],
      });
      fixture.detectChanges();

      expect(root().textContent).toContain('Rescheduled maintenance');
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Blackout period updated.' })]);
    });

    it('deletes a blackout after confirmation', () => {
      configure();
      flushAll({}, [], [{ id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00Z', endUtc: '2027-06-10T17:00:00Z', reason: 'Maintenance' }]);

      buttonWithText('Delete').click();
      fixture.detectChanges();
      dialogButtonWithText('Delete').click();

      httpTesting.expectOne((r) => r.url === `${BLACKOUTS_URL}/b1` && r.method === 'DELETE').flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).toBeNull();
      expect(root().textContent).toContain('No blackout periods configured');
    });

    it('rejects an end that is before or equal to the start, without an HTTP call', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '13:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '09:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();
      fixture.detectChanges();

      expect(root().textContent).toContain('End must be after start.');
      httpTesting.expectNone((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
    });

    it('rejects an identical start and end time, without an HTTP call', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '09:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();
      fixture.detectChanges();

      expect(root().textContent).toContain('End must be after start.');
      httpTesting.expectNone((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
    });

    // America/New_York springs forward on 2027-03-14: clocks jump from 2:00 to 3:00, so every
    // wall-clock time in that gap (e.g. 2:30 AM) never actually happens that day.
    it('rejects a start time that does not exist because of a spring-forward daylight-saving change', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-03-14');
      setValue('#blackout-start-time', '02:30');
      setValue('#blackout-end-date', '2027-03-14');
      setValue('#blackout-end-time', '05:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();
      fixture.detectChanges();

      expect(root().textContent).toContain('daylight-saving time change');
      httpTesting.expectNone((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
    });

    // America/New_York falls back on 2027-11-07: clocks go from 2:00 to 1:00, so 1:30 AM happens
    // twice. This is accepted (not rejected like the nonexistent spring-forward case) and resolves,
    // per the project's documented policy, to the offset in effect just BEFORE the fall-back (EDT,
    // UTC-4) - the first of the two occurrences.
    it('accepts an ambiguous fall-back local time and resolves it to the pre-fall-back offset', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-11-07');
      setValue('#blackout-start-time', '01:30');
      setValue('#blackout-end-date', '2027-11-07');
      setValue('#blackout-end-time', '04:00');
      setValue('#blackout-reason', 'Fall-back test');
      dialogButtonWithText('Add blackout').click();

      const req = httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
      expect(req.request.body.startUtc).toBe('2027-11-07T05:30:00.000Z');
      req.flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, reason: 'Fall-back test',
        conflictingBookingIds: [],
      });
    });

    it('supports a blackout spanning multiple days, including an end exactly at local midnight', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-12');
      setValue('#blackout-end-time', '00:00'); // exactly midnight, two days later
      setValue('#blackout-reason', 'Multi-day maintenance');
      dialogButtonWithText('Add blackout').click();

      // Half-open [startUtc, endUtc): ending exactly at local midnight on the 12th must not spill into
      // the 12th itself - the UTC instant is midnight America/New_York on the 12th, i.e. 2027-06-12T04:00:00Z.
      const req = httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
      expect(req.request.body.startUtc).toBe('2027-06-10T13:00:00.000Z');
      expect(req.request.body.endUtc).toBe('2027-06-12T04:00:00.000Z');
      req.flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: req.request.body.startUtc, endUtc: req.request.body.endUtc, reason: 'Multi-day maintenance',
        conflictingBookingIds: [],
      });
    });

    it('shows a persistent conflict warning (not just a toast) with the affected booking count, and never claims they were cancelled', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '13:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();

      httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST').flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00.000Z', endUtc: '2027-06-10T17:00:00.000Z', reason: 'Maintenance',
        conflictingBookingIds: ['booking-1', 'booking-2'],
      });
      fixture.detectChanges();

      expect(root().textContent).toContain('2 existing bookings now overlap');
      expect(root().textContent).toContain('booking-1, booking-2');
      // Must honestly disclose the bookings were NOT cancelled - never claim (or imply) that they were.
      expect(root().textContent).toContain('were not automatically cancelled and remain active');
      // Not reported via the toast/notification channel - it must stay visible until dismissed, not
      // auto-disappear like a success toast would.
      expect(TestBed.inject(NotificationService).toasts()).toEqual([]);

      (root().querySelector('.manage-availability__dismiss') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(root().textContent).not.toContain('now overlap');
    });

    it('keeps the dialog open with entered values preserved if the API call fails', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '13:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();

      httpTesting
        .expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST')
        .flush({ title: 'Conflict', detail: 'Resource resource-1 is archived and cannot be modified.' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).not.toBeNull();
      expect(root().textContent).toContain('is archived and cannot be modified');
      expect((root().querySelector('#blackout-reason') as HTMLInputElement).value).toBe('Maintenance');
    });

    it('maps backend field errors (e.g. Reason) inline next to the relevant field', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '13:00');
      setValue('#blackout-reason', 'x');
      dialogButtonWithText('Add blackout').click();

      httpTesting
        .expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST')
        .flush({ title: 'Validation failed', errors: { Reason: ["'Reason' must be at most 1000 characters."] } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      const reasonField = root().querySelector('#blackout-reason')!.parentElement!;
      expect(reasonField.textContent).toContain('must be at most 1000 characters');
    });

    it('prevents a duplicate blackout submission while the first request is in flight', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-start-date', '2027-06-10');
      setValue('#blackout-start-time', '09:00');
      setValue('#blackout-end-date', '2027-06-10');
      setValue('#blackout-end-time', '13:00');
      setValue('#blackout-reason', 'Maintenance');
      dialogButtonWithText('Add blackout').click();
      dialogButtonWithText('Add blackout').click();

      httpTesting.expectOne((r) => r.url === BLACKOUTS_URL && r.method === 'POST').flush({
        id: 'b1', resourceId: RESOURCE_ID, startUtc: '2027-06-10T13:00:00.000Z', endUtc: '2027-06-10T17:00:00.000Z', reason: 'Maintenance',
        conflictingBookingIds: [],
      });
    });

    it('closes the dialog without saving when Cancel is clicked', () => {
      configure();
      flushAll();
      openCreateDialog();

      setValue('#blackout-reason', 'Should be discarded');
      buttonWithText('Cancel').click();
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).toBeNull();
      httpTesting.expectNone((r) => r.url === BLACKOUTS_URL && r.method === 'POST');
    });
  });
});
