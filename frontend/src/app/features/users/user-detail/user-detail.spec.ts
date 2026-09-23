import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { AuthService } from '../../../core/auth/auth.service';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ALL_GLOBAL_ROLES, DELEGABLE_ROLES } from '../user.models';
import { UserDetailComponent } from './user-detail';

const USER_ID = 'user-1';
const USER_URL = `${environment.apiUrl}/users/${USER_ID}`;

function wireUser(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: USER_ID,
    firstName: 'Jane',
    lastName: 'Doe',
    email: 'jane@example.com',
    tenantId: 'tenant-1',
    status: 0, // Active
    roles: ['Approver'],
    createdAtUtc: '2026-01-01T00:00:00Z',
    pendingInvitationExpiresAtUtc: null,
    ...overrides,
  };
}

describe('UserDetailComponent', () => {
  let fixture: ComponentFixture<UserDetailComponent>;
  let httpTesting: HttpTestingController;

  function configure(currentUserId = 'admin-id', backQueryParams: Record<string, string> = {}): void {
    TestBed.configureTestingModule({
      imports: [UserDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => USER_ID }, queryParams: backQueryParams } } },
        {
          provide: AuthService,
          useValue: { currentUser: () => ({ userId: currentUserId, email: 'admin@acme.test', tenantId: 'tenant-1', roles: [] }) },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UserDetailComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushLoad(overrides: Partial<Record<string, unknown>> = {}): void {
    httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'GET').flush(wireUser(overrides));
    fixture.detectChanges();
  }

  function buttonWithText(text: string): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  function dialogButtonWithText(text: string): HTMLButtonElement {
    const dialog = root().querySelector('.dialog') as HTMLElement;
    return [...dialog.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as HTMLButtonElement;
  }

  afterEach(() => httpTesting.verify());

  it('shows a loading skeleton before the user loads', () => {
    configure();
    fixture.detectChanges();

    expect(root().querySelector('.user-detail--skeleton')).not.toBeNull();
    httpTesting.expectOne((r) => r.url === USER_URL).flush(wireUser());
  });

  it('shows a not-found panel with a link back to the list for a 404 (deleted or cross-tenant target)', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === USER_URL).flush('not found', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(root().textContent).toContain("doesn't exist");
    const backLink = [...root().querySelectorAll('a')].find((a) => a.textContent?.includes('Back to users'));
    expect(backLink?.getAttribute('href')).toBe('/users');
  });

  it('shows a retry affordance on a failed (non-404) load', () => {
    configure();
    fixture.detectChanges();
    httpTesting.expectOne((r) => r.url === USER_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
    fixture.detectChanges();

    expect(root().textContent).toContain("Couldn't load this user");
    buttonWithText('Try again').click();
    httpTesting.expectOne((r) => r.url === USER_URL).flush(wireUser());
  });

  it('forwards the list\'s query params to the "Back to users" link, unchanged', () => {
    configure('admin-id', { search: 'jane', page: '2' });
    fixture.detectChanges();
    flushLoad();

    const backLink = [...root().querySelectorAll('a')].find((a) => a.textContent?.includes('Back to users'));
    const href = backLink?.getAttribute('href') ?? '';
    expect(href).toContain('search=jane');
    expect(href).toContain('page=2');
  });

  it('marks the signed-in administrator\'s own record as "(You)"', () => {
    configure(USER_ID);
    fixture.detectChanges();
    flushLoad();

    expect(root().textContent).toContain('(You)');
  });

  describe('profile edit', () => {
    it('prefills the form with the loaded user\'s current values', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ firstName: 'Original', lastName: 'Name' });

      expect((root().querySelector('#first-name') as HTMLInputElement).value).toBe('Original');
      expect((root().querySelector('#last-name') as HTMLInputElement).value).toBe('Name');
    });

    it('rejects an empty first name client-side without issuing a request', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      const firstNameInput = root().querySelector('#first-name') as HTMLInputElement;
      firstNameInput.value = '';
      firstNameInput.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      expect(root().textContent).toContain('First name is required.');
      httpTesting.expectNone((r) => r.url === USER_URL && r.method === 'PUT');
    });

    it('submits the trimmed name fields and shows a success toast on a valid update', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      const firstNameInput = root().querySelector('#first-name') as HTMLInputElement;
      firstNameInput.value = '  Updated  ';
      firstNameInput.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      root().querySelector('form')!.dispatchEvent(new Event('submit'));

      const req = httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'PUT');
      expect(req.request.body).toEqual({ firstName: 'Updated', lastName: 'Doe' });
      req.flush(wireUser({ firstName: 'Updated' }));
      fixture.detectChanges();

      expect(root().textContent).toContain('Updated Doe');
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'User updated.' })]);
    });

    it('shows field-level validation errors from the backend', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      httpTesting
        .expectOne((r) => r.url === USER_URL && r.method === 'PUT')
        .flush({ errors: { FirstName: ['FirstName must not exceed 100 characters.'] } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(root().textContent).toContain('FirstName must not exceed 100 characters.');
    });

    it('preserves the entered values in the form after a failed save', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      const firstNameInput = root().querySelector('#first-name') as HTMLInputElement;
      firstNameInput.value = 'StillTyped';
      firstNameInput.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'PUT').flush('boom', { status: 500, statusText: 'Internal Server Error' });
      fixture.detectChanges();

      expect((root().querySelector('#first-name') as HTMLInputElement).value).toBe('StillTyped');
    });

    it('disables the submit button and ignores a second click while a save is already in flight', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      const submitButton = buttonWithText('Saving…');
      expect(submitButton.disabled).toBe(true);

      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'PUT').flush(wireUser());
    });
  });

  describe('deactivate / reactivate', () => {
    it('opens a confirmation dialog explaining the consequences, and does nothing until confirmed', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      buttonWithText('Deactivate user').click();
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).not.toBeNull();
      expect(root().textContent).toContain('no longer be able to log in');
      expect(root().textContent).toContain('15 minutes');
      expect(root().textContent).toContain('not automatically removed');
      expect(root().textContent).not.toContain('permanently delet');
      httpTesting.expectNone((r) => r.url === USER_URL && r.method === 'DELETE');
    });

    it('disables the Deactivate action for the signed-in administrator\'s own record', () => {
      configure(USER_ID);
      fixture.detectChanges();
      flushLoad();

      expect(buttonWithText('Deactivate user').disabled).toBe(true);
    });

    // Frontend state can be stale (or manipulated) even for a non-self target - the backend's own
    // self-lockout check remains the real enforcement, and this proves the friendly message path when
    // it fires despite the button being enabled and clickable here.
    it('shows a friendly message if the backend rejects deactivation as a self-lockout', () => {
      configure('admin-id');
      fixture.detectChanges();
      flushLoad();

      buttonWithText('Deactivate user').click();
      fixture.detectChanges();
      dialogButtonWithText('Deactivate user').click();

      httpTesting
        .expectOne((r) => r.url === USER_URL && r.method === 'DELETE')
        .flush({ detail: 'raw message', errorCode: 'User.SelfLockout' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().textContent).toContain('You cannot deactivate your own account.');
      expect(root().querySelector('.dialog')).not.toBeNull();
    });

    it('shows a friendly message when deactivation is rejected as the last remaining administrator', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      buttonWithText('Deactivate user').click();
      fixture.detectChanges();
      dialogButtonWithText('Deactivate user').click();

      httpTesting
        .expectOne((r) => r.url === USER_URL && r.method === 'DELETE')
        .flush({ detail: 'raw message', errorCode: 'User.LastAdminRemaining' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().textContent).toContain('must always retain at least one active administrator');
    });

    it('deactivates on confirm, closes the dialog, and updates the status badge while keeping roles visible', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver', 'TenantAdmin'] });

      buttonWithText('Deactivate user').click();
      fixture.detectChanges();
      dialogButtonWithText('Deactivate user').click();

      httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'DELETE').flush(wireUser({ status: 2, roles: ['Approver', 'TenantAdmin'] }));
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).toBeNull();
      expect(root().textContent).toContain('Inactive');
      expect(root().textContent).toContain('Approver');
      expect(root().textContent).toContain('TenantAdmin');
      expect(TestBed.inject(NotificationService).toasts().length).toBe(1);
    });

    it('keeps the status shown as Active if deactivation fails', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      buttonWithText('Deactivate user').click();
      fixture.detectChanges();
      dialogButtonWithText('Deactivate user').click();

      httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'DELETE').flush('boom', { status: 500, statusText: 'Internal Server Error' });
      fixture.detectChanges();

      expect(root().textContent).toContain('Active');
      expect(root().textContent).not.toContain('Inactive');
    });

    it('shows Reactivate (not Deactivate) for an Inactive user, and reactivating updates the badge without claiming sessions are restored', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ status: 2 });

      expect(buttonWithText('Deactivate user')).toBeUndefined();
      const reactivateButton = buttonWithText('Reactivate user');
      expect(reactivateButton).toBeTruthy();
      expect(root().textContent).toContain('does not restore');
      expect(root().textContent).toContain('sign in again');

      reactivateButton.click();
      httpTesting.expectOne((r) => r.url === `${USER_URL}/reactivate` && r.method === 'POST').flush(wireUser({ status: 0 }));
      fixture.detectChanges();

      expect(root().textContent).toContain('Active');
      const toast = TestBed.inject(NotificationService).toasts()[0];
      expect(toast.message).not.toMatch(/restored/i);
      expect(toast.message).toMatch(/log in again/i);
    });
  });

  describe('role management', () => {
    it('never renders ResourceApprover as an assignable or displayed global role', () => {
      expect(ALL_GLOBAL_ROLES).not.toContain('ResourceApprover');
      expect(DELEGABLE_ROLES).not.toContain('ResourceApprover');

      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver'] });

      expect(root().textContent).not.toContain('ResourceApprover');
    });

    it('offers "Add a role" buttons only for roles not already held, excluding SysAdmin entirely', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver'] });

      const addButtons = [...root().querySelectorAll('button')].filter((b) => b.textContent?.trim().startsWith('+'));
      const labels = addButtons.map((b) => b.textContent!.trim());
      expect(labels).toContain('+ Member');
      expect(labels).toContain('+ TenantAdmin');
      expect(labels).not.toContain('+ Approver');
      expect(labels).not.toContain('+ SysAdmin');
    });

    it('explains that Approver still requires a per-resource assignment', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: [] });

      expect(root().textContent).toContain('must still be assigned to individual resources');
    });

    it('assigns a role and updates the chips with the server-returned role list', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: [] });

      buttonWithText('+ Member').click();

      const req = httpTesting.expectOne((r) => r.url === `${USER_URL}/roles` && r.method === 'POST');
      expect(req.request.body).toEqual({ role: 'Member' });
      req.flush({ userId: USER_ID, roles: ['Member'] });
      fixture.detectChanges();

      const roleChips = [...root().querySelectorAll('.role-chip')].map((el) => el.textContent?.trim());
      expect(roleChips).toEqual(['Member']);
    });

    it('handles a duplicate-role conflict by refreshing state and showing a clear message', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: [] });

      buttonWithText('+ Approver').click();
      httpTesting
        .expectOne((r) => r.url === `${USER_URL}/roles`)
        .flush({ detail: 'raw', errorCode: 'User.RoleConflict' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().textContent).toContain('already assigned');
      httpTesting.expectOne((r) => r.url === USER_URL && r.method === 'GET').flush(wireUser({ roles: ['Approver'] }));
    });

    it('asks for confirmation before removing the Approver role, explaining the loss of approval eligibility', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver'] });

      buttonWithText('Remove').click();
      fixture.detectChanges();

      expect(root().querySelector('.dialog')).not.toBeNull();
      expect(root().textContent).toContain('lose global approval eligibility');
      httpTesting.expectNone((r) => r.url === `${USER_URL}/roles/Approver}` && r.method === 'DELETE');
    });

    it('removes the role on confirm and updates the chips', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver', 'Member'] });

      buttonWithText('Remove').click();
      fixture.detectChanges();
      dialogButtonWithText('Remove role').click();

      httpTesting.expectOne((r) => r.url === `${USER_URL}/roles/Approver` && r.method === 'DELETE').flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const roleChips = [...root().querySelectorAll('.role-chip')].map((el) => el.textContent?.trim());
      expect(roleChips).toEqual(['Member']);
      expect(root().querySelector('.dialog')).toBeNull();
    });

    it('shows the exact ResourceApprover-assignment-conflict explanation, and does not remove the chip', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['Approver'] });

      buttonWithText('Remove').click();
      fixture.detectChanges();
      dialogButtonWithText('Remove role').click();

      httpTesting
        .expectOne((r) => r.url === `${USER_URL}/roles/Approver` && r.method === 'DELETE')
        .flush({ detail: 'raw', errorCode: 'User.ApproverAssignmentsExist' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().textContent).toContain('still assigned as an approver for one or more resources');
      expect(root().textContent).toContain('Remove those resource assignments first');
      // No automatic cleanup call of any kind - the only requests made are the initial load and this
      // one rejected removal attempt, verified in full by afterEach's httpTesting.verify().
      const roleChips = [...root().querySelectorAll('.role-chip')].map((el) => el.textContent?.trim());
      expect(roleChips).toEqual(['Approver']);
    });

    it('shows a friendly message when role removal is rejected as the last remaining administrator', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: ['TenantAdmin'] });

      buttonWithText('Remove').click();
      fixture.detectChanges();
      dialogButtonWithText('Remove role').click();

      httpTesting
        .expectOne((r) => r.url === `${USER_URL}/roles/TenantAdmin` && r.method === 'DELETE')
        .flush({ detail: 'raw', errorCode: 'User.LastAdminRemaining' }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(root().textContent).toContain('must always retain at least one active administrator');
    });

    it('disables removing TenantAdmin from the signed-in administrator\'s own record', () => {
      configure(USER_ID);
      fixture.detectChanges();
      flushLoad({ roles: ['TenantAdmin'] });

      const removeButton = buttonWithText('Remove');
      expect(removeButton.disabled).toBe(true);
    });

    it('still allows the signed-in administrator to remove their own non-administrative role', () => {
      configure(USER_ID);
      fixture.detectChanges();
      flushLoad({ roles: ['Approver'] });

      expect(buttonWithText('Remove').disabled).toBe(false);
    });

    it('shows a persistent note about JWT/session role staleness', () => {
      configure();
      fixture.detectChanges();
      flushLoad();

      expect(root().textContent).toContain('15 minutes');
    });

    it('does not disable unrelated controls (Save changes) while a role assignment is in flight', () => {
      configure();
      fixture.detectChanges();
      flushLoad({ roles: [] });

      buttonWithText('+ Member').click();
      fixture.detectChanges();

      expect(buttonWithText('Save changes').disabled).toBe(false);
      httpTesting.expectOne((r) => r.url === `${USER_URL}/roles`).flush({ userId: USER_ID, roles: ['Member'] });
    });
  });
});
