import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Router, UrlTree, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../../app.routes';
import { AuthService } from '../../../core/auth/auth.service';
import { environment } from '../../../../environments/environment';
import { UserListComponent } from './user-list';

const USERS_URL = `${environment.apiUrl}/users`;
const SEARCH_DEBOUNCE_MS = 300;

@Component({ selector: 'app-empty-stand-in', template: '' })
class EmptyStandInComponent {}

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

function pagedUsers(items: unknown[], overrides: Partial<Record<string, unknown>> = {}) {
  return { items, page: 1, pageSize: 20, totalCount: items.length, ...overrides };
}

describe('UserListComponent', () => {
  let harness: RouterTestingHarness;
  let httpTesting: HttpTestingController;

  async function configure(
    url = '/users',
    hasAnyRole: (...roles: string[]) => boolean = () => true,
    currentUserId = 'admin-1',
  ): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'users', component: UserListComponent },
          { path: 'users/:id', component: EmptyStandInComponent },
        ]),
        {
          provide: AuthService,
          useValue: { hasAnyRole, currentUser: () => ({ userId: currentUserId, email: 'admin@acme.test', tenantId: 'tenant-1', roles: [] }) },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create(url);
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function flushList(items: unknown[], overrides: Partial<Record<string, unknown>> = {}): void {
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers(items, overrides));
    harness.detectChanges();
  }

  function setSearchTerm(term: string): void {
    const input = root().querySelector('#user-search') as HTMLInputElement;
    input.value = term;
    input.dispatchEvent(new Event('input'));
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    httpTesting.verify();
    vi.useRealTimers();
  });

  it('shows a loading skeleton before the list loads', async () => {
    await configure();
    expect(root().querySelector('.user-list__table[aria-busy]')).not.toBeNull();
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([]));
  });

  it('shows an empty state when there are no matching users', async () => {
    await configure();
    flushList([]);

    expect(root().textContent).toContain('No users found');
  });

  it('shows a retry affordance on a failed load, and retry re-issues the request', async () => {
    await configure();
    httpTesting.expectOne((r) => r.url === USERS_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
    harness.detectChanges();

    expect(root().textContent).toContain("Couldn't load users");

    const retryButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.includes('Try again')) as HTMLButtonElement;
    retryButton.click();

    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([]));
    harness.detectChanges();
    expect(root().textContent).toContain('No users found');
  });

  it('renders each user with their status badge and role chips', async () => {
    await configure();
    flushList([wireUser({ status: 0, roles: ['Approver', 'TenantAdmin'] })]);

    const text = root().textContent ?? '';
    expect(text).toContain('Jane Doe');
    expect(text).toContain('jane@example.com');
    expect(text).toContain('Active');
    expect(text).toContain('Approver');
    expect(text).toContain('TenantAdmin');
  });

  it('shows an Invited user with no roles and no invitation-token/action controls', async () => {
    await configure();
    flushList([wireUser({ status: 1, roles: [] })]);

    const text = root().textContent ?? '';
    expect(text).toContain('Invited');
    expect(text).not.toMatch(/token/i);
    expect(root().querySelector('button[aria-label*="nvit" i]')).toBeNull();
  });

  it('marks the signed-in administrator\'s own row as "(You)"', async () => {
    await configure('/users', () => true, 'user-1');
    flushList([wireUser({ id: 'user-1' }), wireUser({ id: 'user-2', firstName: 'Sam', lastName: 'Lee' })]);

    const rows = [...root().querySelectorAll('.user-row')];
    expect(rows[0].textContent).toContain('(You)');
    expect(rows[1].textContent).not.toContain('(You)');
  });

  it('debounces search input, firing exactly one request for several rapid keystrokes', async () => {
    await configure();
    flushList([]);

    setSearchTerm('j');
    setSearchTerm('ja');
    setSearchTerm('jane');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS - 1);
    httpTesting.expectNone((r) => r.url === USERS_URL && r.params.has('search'));

    vi.advanceTimersByTime(1);
    const req = httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('search') === 'jane');
    req.flush(pagedUsers([]));
  });

  it('cancels a stale in-flight search when a newer term is typed (switchMap)', async () => {
    await configure();
    flushList([]);

    setSearchTerm('sta');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    const staleReq = httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('search') === 'sta');

    setSearchTerm('stale-but-newer');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
    const freshReq = httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('search') === 'stale-but-newer');

    expect(staleReq.cancelled).toBe(true);
    freshReq.flush(pagedUsers([wireUser({ firstName: 'Fresh', lastName: 'Result' })]));
    harness.detectChanges();

    expect(root().textContent).toContain('Fresh Result');
  });

  it('resets to page 1 and reloads immediately (no debounce) when the role filter changes', async () => {
    await configure('/users?page=3');
    httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('page') === '3').flush(pagedUsers([], { page: 3, totalCount: 100 }));
    harness.detectChanges();

    const select = root().querySelector('#role-filter') as HTMLSelectElement;
    select.value = 'Approver';
    select.dispatchEvent(new Event('change'));

    const req = httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('role') === 'Approver');
    expect(req.request.params.get('page')).toBe('1');
    req.flush(pagedUsers([]));
  });

  it('resets to page 1 and reloads immediately (no debounce) when the status filter changes', async () => {
    await configure();
    flushList([]);

    const select = root().querySelector('#status-filter') as HTMLSelectElement;
    select.value = 'Inactive';
    select.dispatchEvent(new Event('change'));

    const req = httpTesting.expectOne((r) => r.url === USERS_URL && r.params.get('status') === 'Inactive');
    expect(req.request.params.get('page')).toBe('1');
    req.flush(pagedUsers([]));
  });

  it('paginates using the total count returned by the server, not a client-side slice', async () => {
    await configure();
    flushList(
      Array.from({ length: 20 }, (_, i) => wireUser({ id: `u${i}` })),
      { totalCount: 45 },
    );

    expect(root().textContent).toContain('Page 1 of 3');
    const nextButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Next') as HTMLButtonElement;
    nextButton.click();

    const req = httpTesting.expectOne((r) => r.url === USERS_URL);
    expect(req.request.params.get('page')).toBe('2');
    req.flush(pagedUsers([], { page: 2, totalCount: 45 }));
  });

  it('loads its initial search/role/status/page from the URL\'s own query params', async () => {
    await configure('/users?search=jane&role=Approver&status=Active&page=2');

    const req = httpTesting.expectOne((r) => r.url === USERS_URL);
    expect(req.request.params.get('search')).toBe('jane');
    expect(req.request.params.get('role')).toBe('Approver');
    expect(req.request.params.get('status')).toBe('Active');
    expect(req.request.params.get('page')).toBe('2');
    req.flush(pagedUsers([wireUser()], { page: 2, totalCount: 30 }));
  });

  it('forwards the current filters/page to each user row\'s link, so editing a user preserves list state on return', async () => {
    await configure('/users?search=jane&role=Approver&status=Active&page=2');
    httpTesting.expectOne((r) => r.url === USERS_URL).flush(pagedUsers([wireUser({ id: 'user-1' })], { page: 2, totalCount: 30 }));
    harness.detectChanges();

    const link = root().querySelector('.user-row') as HTMLAnchorElement;
    const href = link.getAttribute('href') ?? '';
    expect(href).toContain('/users/user-1');
    expect(href).toContain('search=jane');
    expect(href).toContain('role=Approver');
    expect(href).toContain('status=Active');
    expect(href).toContain('page=2');
  });

  describe('route protection (app.routes.ts wiring)', () => {
    let authService: { hasAnyRole: ReturnType<typeof vi.fn> };

    beforeEach(() => {
      authService = { hasAnyRole: vi.fn() };
      TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: authService }] });
    });

    function guardFor(path: string): CanActivateFn {
      const shellRoute = routes.find((r) => r.path === '')!;
      const route = shellRoute.children!.find((r) => r.path === path)!;
      return route.canActivate![0] as CanActivateFn;
    }

    it('blocks a plain Member/Approver from both /users and /users/:id', () => {
      authService.hasAnyRole.mockReturnValue(false);
      const router = TestBed.inject(Router);

      const usersResult = TestBed.runInInjectionContext(() => guardFor('users')(null!, null!));
      const detailResult = TestBed.runInInjectionContext(() => guardFor('users/:id')(null!, null!));

      expect(router.serializeUrl(usersResult as UrlTree)).toBe('/');
      expect(router.serializeUrl(detailResult as UrlTree)).toBe('/');
    });

    it('allows TenantAdmin/SysAdmin to reach both /users and /users/:id', () => {
      authService.hasAnyRole.mockReturnValue(true);

      expect(TestBed.runInInjectionContext(() => guardFor('users')(null!, null!))).toBe(true);
      expect(TestBed.runInInjectionContext(() => guardFor('users/:id')(null!, null!))).toBe(true);
    });
  });
});
