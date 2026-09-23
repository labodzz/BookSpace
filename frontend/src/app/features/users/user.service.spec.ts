import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { UserService } from './user.service';

const USERS_URL = `${environment.apiUrl}/users`;

function wireUser(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'user-1',
    firstName: 'Jane',
    lastName: 'Doe',
    email: 'jane@example.com',
    tenantId: 'tenant-1',
    status: 0,
    roles: ['Approver'],
    ...overrides,
  };
}

describe('UserService', () => {
  let service: UserService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(UserService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  describe('getUsers', () => {
    it('sends only page and pageSize when no options are given', () => {
      service.getUsers(1, 20).subscribe();

      const req = httpTesting.expectOne((r) => r.url === USERS_URL);
      expect(req.request.params.get('page')).toBe('1');
      expect(req.request.params.get('pageSize')).toBe('20');
      expect(req.request.params.has('search')).toBe(false);
      expect(req.request.params.has('role')).toBe(false);
      expect(req.request.params.has('status')).toBe(false);
      expect(req.request.params.has('ids')).toBe(false);
      req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    });

    it('sends search, role, and status when given', () => {
      service.getUsers(1, 20, { search: 'jane', role: 'Approver', status: 'Active' }).subscribe();

      const req = httpTesting.expectOne((r) => r.url === USERS_URL);
      expect(req.request.params.get('search')).toBe('jane');
      expect(req.request.params.get('role')).toBe('Approver');
      expect(req.request.params.get('status')).toBe('Active');
      req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    });

    it('sends each id as its own repeated query param, not a single comma-joined value', () => {
      service.getUsers(1, 2, { ids: ['id-1', 'id-2'] }).subscribe();

      const req = httpTesting.expectOne((r) => r.url === USERS_URL);
      expect(req.request.params.getAll('ids')).toEqual(['id-1', 'id-2']);
      req.flush({ items: [], page: 1, pageSize: 2, totalCount: 0 });
    });

    it('omits search/role/status/ids entirely when they are empty rather than sending blank values', () => {
      service.getUsers(1, 20, { search: '', role: '', ids: [] }).subscribe();

      const req = httpTesting.expectOne((r) => r.url === USERS_URL);
      expect(req.request.params.has('search')).toBe(false);
      expect(req.request.params.has('role')).toBe(false);
      expect(req.request.params.has('status')).toBe(false);
      expect(req.request.params.has('ids')).toBe(false);
      req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    });

    it('passes the roles field on each returned user through unchanged', () => {
      let result: unknown;
      service.getUsers(1, 20).subscribe((page) => (result = page));

      httpTesting.expectOne((r) => r.url === USERS_URL).flush({ items: [wireUser({ roles: ['Approver', 'TenantAdmin'] })], page: 1, pageSize: 20, totalCount: 1 });

      expect((result as { items: { roles: string[] }[] }).items[0].roles).toEqual(['Approver', 'TenantAdmin']);
    });

    it('maps each user\'s raw numeric status onto its string form (0=Active, 1=Invited, 2=Inactive)', () => {
      let result: unknown;
      service.getUsers(1, 20).subscribe((page) => (result = page));

      httpTesting.expectOne((r) => r.url === USERS_URL).flush({
        items: [wireUser({ id: 'u1', status: 0 }), wireUser({ id: 'u2', status: 1 }), wireUser({ id: 'u3', status: 2 })],
        page: 1,
        pageSize: 20,
        totalCount: 3,
      });

      expect((result as { items: { status: string }[] }).items.map((u) => u.status)).toEqual(['Active', 'Invited', 'Inactive']);
    });

    it('falls back to Active for an unrecognized status code instead of crashing', () => {
      let result: unknown;
      service.getUsers(1, 20).subscribe((page) => (result = page));

      httpTesting.expectOne((r) => r.url === USERS_URL).flush({ items: [wireUser({ status: 99 })], page: 1, pageSize: 20, totalCount: 1 });

      expect((result as { items: { status: string }[] }).items[0].status).toBe('Active');
    });
  });

  describe('getUser', () => {
    it('requests the single-user endpoint and maps its status', () => {
      let result: unknown;
      service.getUser('user-1').subscribe((u) => (result = u));

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1` && r.method === 'GET')
        .flush({ ...wireUser(), status: 2, createdAtUtc: '2026-01-01T00:00:00Z', pendingInvitationExpiresAtUtc: null });

      expect((result as { status: string }).status).toBe('Inactive');
    });

    it('surfaces a 404 as a plain HTTP error for the caller to interpret', () => {
      let error: unknown;
      service.getUser('missing').subscribe({ error: (e) => (error = e) });

      httpTesting.expectOne((r) => r.url === `${USERS_URL}/missing`).flush('not found', { status: 404, statusText: 'Not Found' });

      expect((error as { status: number }).status).toBe(404);
    });
  });

  describe('updateUser', () => {
    it('sends firstName/lastName and maps the response', () => {
      let result: unknown;
      service.updateUser('user-1', { firstName: 'New', lastName: 'Name' }).subscribe((u) => (result = u));

      const req = httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1` && r.method === 'PUT');
      expect(req.request.body).toEqual({ firstName: 'New', lastName: 'Name' });
      req.flush({ ...wireUser({ firstName: 'New', lastName: 'Name' }), createdAtUtc: '2026-01-01T00:00:00Z', pendingInvitationExpiresAtUtc: null });

      expect((result as { firstName: string }).firstName).toBe('New');
    });

    it('propagates a duplicate/validation field-error body unchanged for the caller to map', () => {
      let error: unknown;
      service.updateUser('user-1', { firstName: '', lastName: 'Name' }).subscribe({ error: (e) => (error = e) });

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1` && r.method === 'PUT')
        .flush({ errors: { FirstName: ['FirstName must not be empty.'] } }, { status: 400, statusText: 'Bad Request' });

      expect((error as { status: number }).status).toBe(400);
    });
  });

  describe('deactivateUser', () => {
    it('sends a DELETE and maps the returned Inactive status', () => {
      let result: unknown;
      service.deactivateUser('user-1').subscribe((u) => (result = u));

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1` && r.method === 'DELETE')
        .flush({ ...wireUser({ status: 2 }), createdAtUtc: '2026-01-01T00:00:00Z', pendingInvitationExpiresAtUtc: null });

      expect((result as { status: string }).status).toBe('Inactive');
    });

    it('surfaces a self-lockout conflict for the caller to interpret', () => {
      let error: unknown;
      service.deactivateUser('self-id').subscribe({ error: (e) => (error = e) });

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/self-id` && r.method === 'DELETE')
        .flush({ detail: 'You cannot deactivate your own account.', errorCode: 'User.SelfLockout' }, { status: 409, statusText: 'Conflict' });

      expect((error as { error: { errorCode: string } }).error.errorCode).toBe('User.SelfLockout');
    });
  });

  describe('reactivateUser', () => {
    it('sends a POST with no body and maps the returned Active status', () => {
      let result: unknown;
      service.reactivateUser('user-1').subscribe((u) => (result = u));

      const req = httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1/reactivate` && r.method === 'POST');
      expect(req.request.body).toBeNull();
      req.flush({ ...wireUser({ status: 0 }), createdAtUtc: '2026-01-01T00:00:00Z', pendingInvitationExpiresAtUtc: null });

      expect((result as { status: string }).status).toBe('Active');
    });

    it('surfaces a status conflict (already active) for the caller to interpret', () => {
      let error: unknown;
      service.reactivateUser('user-1').subscribe({ error: (e) => (error = e) });

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1/reactivate`)
        .flush({ detail: 'User is already active.', errorCode: 'User.StatusConflict' }, { status: 409, statusText: 'Conflict' });

      expect((error as { error: { errorCode: string } }).error.errorCode).toBe('User.StatusConflict');
    });
  });

  describe('assignRole', () => {
    it('sends the role in the body and returns the updated role list', () => {
      let result: unknown;
      service.assignRole('user-1', 'Approver').subscribe((r) => (result = r));

      const req = httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1/roles` && r.method === 'POST');
      expect(req.request.body).toEqual({ role: 'Approver' });
      req.flush({ userId: 'user-1', roles: ['Member', 'Approver'] });

      expect((result as { roles: string[] }).roles).toEqual(['Member', 'Approver']);
    });

    it('surfaces a duplicate-role conflict for the caller to interpret', () => {
      let error: unknown;
      service.assignRole('user-1', 'Approver').subscribe({ error: (e) => (error = e) });

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1/roles`)
        .flush({ detail: 'Already holds this role.', errorCode: 'User.RoleConflict' }, { status: 409, statusText: 'Conflict' });

      expect((error as { error: { errorCode: string } }).error.errorCode).toBe('User.RoleConflict');
    });
  });

  describe('removeRole', () => {
    it('sends a DELETE to the role-scoped URL', () => {
      service.removeRole('user-1', 'Approver').subscribe();

      const req = httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1/roles/Approver` && r.method === 'DELETE');
      req.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('surfaces the ResourceApprover-assignment conflict for the caller to interpret', () => {
      let error: unknown;
      service.removeRole('user-1', 'Approver').subscribe({ error: (e) => (error = e) });

      httpTesting
        .expectOne((r) => r.url === `${USERS_URL}/user-1/roles/Approver`)
        .flush(
          { detail: 'Still assigned as an approver for 1 resource(s).', errorCode: 'User.ApproverAssignmentsExist' },
          { status: 409, statusText: 'Conflict' },
        );

      expect((error as { error: { errorCode: string } }).error.errorCode).toBe('User.ApproverAssignmentsExist');
    });

    it('surfaces a not-found (role already removed / cross-tenant target) for the caller to interpret', () => {
      let error: unknown;
      service.removeRole('user-1', 'TenantAdmin').subscribe({ error: (e) => (error = e) });

      httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1/roles/TenantAdmin`).flush('not found', { status: 404, statusText: 'Not Found' });

      expect((error as { status: number }).status).toBe(404);
    });

    it('surfaces a forbidden response for the caller to interpret', () => {
      let error: unknown;
      service.removeRole('user-1', 'TenantAdmin').subscribe({ error: (e) => (error = e) });

      httpTesting.expectOne((r) => r.url === `${USERS_URL}/user-1/roles/TenantAdmin`).flush('forbidden', { status: 403, statusText: 'Forbidden' });

      expect((error as { status: number }).status).toBe(403);
    });
  });
});
