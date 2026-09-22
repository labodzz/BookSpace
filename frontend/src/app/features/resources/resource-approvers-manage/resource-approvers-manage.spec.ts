import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { environment } from '../../../../environments/environment';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceApproversManageComponent } from './resource-approvers-manage';

const RESOURCE_ID = 'resource-1';
const RESOURCE_URL = `${environment.apiUrl}/resources/${RESOURCE_ID}`;
const APPROVERS_URL = `${RESOURCE_URL}/approvers`;
const USERS_URL = `${environment.apiUrl}/users`;
const SEARCH_DEBOUNCE_MS = 300;

function wireResource(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: RESOURCE_ID,
    resourceTypeId: 'type-1',
    name: 'Falcon Room',
    description: null,
    capacity: 4,
    requiresApproval: true,
    status: 0, // ResourceStatus.Active
    timeZoneId: 'UTC',
    ...overrides,
  };
}

function wireUser(overrides: Partial<Record<string, unknown>> = {}) {
  return { id: 'user-1', firstName: 'Jane', lastName: 'Doe', email: 'jane@example.com', tenantId: 'tenant-1', roles: ['Approver'], ...overrides };
}

function pagedUsers(items: unknown[]) {
  return { items, page: 1, pageSize: 20, totalCount: items.length };
}

describe('ResourceApproversManageComponent', () => {
  let fixture: ComponentFixture<ResourceApproversManageComponent>;
  let httpTesting: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      imports: [ResourceApproversManageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => RESOURCE_ID } } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceApproversManageComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  // Flushes the resource load and its approver list. The debounced initial candidate search that the
  // constructor also kicks off is deliberately left untouched here - it never issues an HTTP request
  // unless fake time is advanced past SEARCH_DEBOUNCE_MS, so tests that don't care about the picker
  // never need to deal with it.
  function flushResourceAndApprovers(resourceOverrides: Partial<Record<string, unknown>> = {}, approvers: unknown[] = []): void {
    httpTesting.expectOne((r) => r.url === RESOURCE_URL && r.method === 'GET').flush(wireResource(resourceOverrides));
    const approversReq = httpTesting.expectOne((r) => r.url === APPROVERS_URL && r.method === 'GET');
    approversReq.flush(approvers);
    if (approvers.length > 0) {
      httpTesting.expectOne((r) => r.url === USERS_URL && r.method === 'GET').flush(pagedUsers([]));
    }
    fixture.detectChanges();
  }

  // Advances past the search debounce and flushes whatever candidate search that triggers - used by
  // both the initial (empty-term) search fired from the constructor and any subsequent typed search.
  function flushSearch(users: unknown[]): void {
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    httpTesting.expectOne((r) => r.url.startsWith(USERS_URL) && r.method === 'GET').flush(pagedUsers(users));
    fixture.detectChanges();
  }

  function setSearchTerm(term: string): void {
    const input = root().querySelector('#approver-search') as HTMLInputElement;
    input.value = term;
    input.dispatchEvent(new Event('input'));
  }

  function buttonWithText(text: string): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  function dialogButtonWithText(text: string): HTMLButtonElement {
    const dialog = root().querySelector('.dialog') as HTMLElement;
    return [...dialog.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    httpTesting.verify();
    vi.useRealTimers();
  });

  it('shows a loading skeleton before the resource loads', () => {
    configure();
    fixture.detectChanges();

    expect(root().querySelector('.manage-approvers--skeleton')).not.toBeNull();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource());
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([]);
  });

  it('shows an empty-state explanation when no approvers are assigned', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();

    expect(root().textContent).toContain('No approvers are assigned to this resource yet.');
  });

  it('shows a prominent warning when RequiresApproval is true and no approver is assigned', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({ requiresApproval: true });

    expect(root().textContent).toContain('has no approver assigned');
  });

  it('does not show the invalid-configuration warning once at least one approver is assigned', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({ requiresApproval: true }, [{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);

    expect(root().textContent).not.toContain('has no approver assigned');
  });

  it('explains that assigned approvers have no work when RequiresApproval is false', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({ requiresApproval: false }, [{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);

    expect(root().textContent).toContain('does not currently require approval');
  });

  it('displays a resolved approver with their name, email, and roles', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource());
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    expect(root().textContent).toContain('Jane Doe');
    expect(root().textContent).toContain('jane@example.com');
    expect(root().textContent).toContain('Approver');
  });

  it('shows a clear warning for an assignment whose user no longer holds an approval-capable role, without hiding it', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource());
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser({ roles: ['Member'] })]));
    fixture.detectChanges();

    expect(root().textContent).toContain('Jane Doe');
    expect(root().textContent).toContain('no longer holds a role that can approve bookings');
  });

  it('shows a clear warning for a legacy assignment whose user cannot be found at all, without hiding it', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource());
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'ghost-user' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([]));
    fixture.detectChanges();

    expect(root().textContent).toContain('Unknown user');
    expect(root().textContent).toContain('could no longer be found');
  });

  it('hides the "Add an approver" section and shows an archived notice for an archived resource, but still allows removal', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource({ status: 3 })); // Archived
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    expect(root().textContent).toContain('This resource is archived');
    expect(root().querySelector('#approver-search')).toBeNull();
    expect(buttonWithText('Remove')).toBeTruthy();
  });

  it('debounces the eligible-approver search and excludes users already assigned', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({}, [{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);

    setSearchTerm('ja');
    setSearchTerm('jan');
    setSearchTerm('jane');
    // Only the debounce's trailing edge should ever fire a request - not one per keystroke.
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS - 1);
    httpTesting.expectNone((r) => r.url.startsWith(USERS_URL));

    flushSearch([wireUser({ id: 'user-1' }), wireUser({ id: 'user-2', firstName: 'Sam', lastName: 'Lee', email: 'sam@example.com' })]);

    expect(root().textContent).toContain('Sam Lee');
    expect(root().textContent).not.toContain('Jane Doe'); // already assigned - excluded from candidates
  });

  it('cancels a stale in-flight search when a newer term is typed (switchMap)', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();

    setSearchTerm('sta');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    const staleReq = httpTesting.expectOne((r) => r.url.startsWith(USERS_URL));

    setSearchTerm('stale-but-newer');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    const freshReq = httpTesting.expectOne((r) => r.url.startsWith(USERS_URL));

    // switchMap already unsubscribed the stale request the moment the newer term's search started -
    // HttpTestingController marks it cancelled, and it must never be observed by the component even if
    // a slow server response for it arrived after the fact.
    expect(staleReq.cancelled).toBe(true);
    freshReq.flush(pagedUsers([wireUser({ id: 'fresh-user', firstName: 'Fresh', lastName: 'Result' })]));
    fixture.detectChanges();

    expect(root().textContent).toContain('Fresh Result');
    expect(root().textContent).not.toContain('Stale Result');
  });

  it('assigns a candidate, adds them to the current-approvers list, and shows a success toast', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();
    flushSearch([wireUser()]);

    buttonWithText('Add').click();
    const req = httpTesting.expectOne((r) => r.url === APPROVERS_URL && r.method === 'POST');
    expect(req.request.body).toEqual({ userId: 'user-1' });
    req.flush({ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' });
    fixture.detectChanges();

    expect(root().textContent).toContain('Jane Doe');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Jane Doe added as an approver.' })]);
    // Now assigned, so no longer offered as a candidate.
    expect(buttonWithText('Add')).toBeFalsy();
  });

  it('disables the Add button while a submission is in flight, preventing a duplicate click', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();
    flushSearch([wireUser()]);

    buttonWithText('Adding…')?.click(); // not yet in-flight, should be absent
    expect(buttonWithText('Adding…')).toBeFalsy();
    buttonWithText('Add').click();
    fixture.detectChanges();

    expect(buttonWithText('Add')).toBeFalsy();
    const addingButton = buttonWithText('Adding…');
    expect(addingButton.disabled).toBe(true);

    httpTesting.expectOne((r) => r.url === APPROVERS_URL && r.method === 'POST').flush({ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' });
  });

  it('on a duplicate/concurrent assignment conflict, shows a clear message and refreshes the current assignments', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();
    flushSearch([wireUser()]);

    buttonWithText('Add').click();
    httpTesting
      .expectOne((r) => r.url === APPROVERS_URL && r.method === 'POST')
      .flush({ title: 'Conflict', errorCode: 'ResourceApprover.Conflict' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().textContent).toContain('already assigned');
    // The failure refreshes the assignment list from the server.
    httpTesting.expectOne((r) => r.url === APPROVERS_URL && r.method === 'GET').flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL && r.method === 'GET').flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    expect(root().textContent).toContain('Jane Doe');
  });

  it('on assignment failure, keeps the picker usable and shows the backend error inline', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers();
    flushSearch([wireUser()]);

    buttonWithText('Add').click();
    httpTesting
      .expectOne((r) => r.url === APPROVERS_URL && r.method === 'POST')
      .flush({ title: 'Conflict', detail: 'This user does not hold an approval-capable role.', errorCode: 'ResourceApprover.RoleRequired' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().textContent).toContain('This user does not hold an approval-capable role.');
    expect(buttonWithText('Add')).toBeTruthy(); // still offered - the picker stays usable
  });

  it('opens a confirmation dialog naming the user and resource before removing an approver', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource({ requiresApproval: false }));
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    buttonWithText('Remove').click();
    fixture.detectChanges();

    const dialog = root().querySelector('.dialog') as HTMLElement;
    expect(dialog.textContent).toContain('Jane Doe');
    expect(dialog.textContent).toContain('Falcon Room');
    httpTesting.expectNone((r) => r.url === `${APPROVERS_URL}/user-1` && r.method === 'DELETE');
  });

  it('warns in the dialog when removing would leave a RequiresApproval resource with no approver', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({ requiresApproval: true }, [{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);

    buttonWithText('Remove').click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')!.textContent).toContain('only approver assigned to this resource');
  });

  it('removes the approver on confirm, closes the dialog, and shows a success toast', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource({ requiresApproval: false }));
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    buttonWithText('Remove').click();
    fixture.detectChanges();
    dialogButtonWithText('Remove approver').click();

    httpTesting.expectOne((r) => r.url === `${APPROVERS_URL}/user-1` && r.method === 'DELETE').flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain('No approvers are assigned to this resource yet.');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Approver removed.' })]);
  });

  it('on a backend final-approver rejection, keeps the assignment visible and shows the explanation, without a duplicate toast', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === RESOURCE_URL).flush(wireResource({ requiresApproval: true }));
    httpTesting.expectOne((r) => r.url === APPROVERS_URL).flush([{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser()]));
    fixture.detectChanges();

    buttonWithText('Remove').click();
    fixture.detectChanges();
    dialogButtonWithText('Remove approver').click();

    httpTesting
      .expectOne((r) => r.url === `${APPROVERS_URL}/user-1` && r.method === 'DELETE')
      .flush({ title: 'Conflict', errorCode: 'ResourceApprover.LastRemaining' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().textContent).toContain('Assign a replacement first');
    expect(TestBed.inject(NotificationService).toasts()).toEqual([]);
    // The assignment must still be there - it was never optimistically removed.
    expect(root().textContent).toContain('Jane Doe');
  });

  it('closes the dialog without removing when "Keep approver" is clicked', () => {
    configure();
    fixture.detectChanges();
    flushResourceAndApprovers({ requiresApproval: false }, [{ id: 'a1', resourceId: RESOURCE_ID, userId: 'user-1' }]);

    buttonWithText('Remove').click();
    fixture.detectChanges();
    dialogButtonWithText('Keep approver').click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    httpTesting.expectNone((r) => r.url === `${APPROVERS_URL}/user-1` && r.method === 'DELETE');
  });
});
