import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth.service';
import { AuthTokens, CurrentUser } from './core/auth/auth.models';
import { TokenStorageService } from './core/auth/token-storage.service';
import { authInterceptor } from './core/http/auth.interceptor';
import { DiagnosticsService } from './core/diagnostics/diagnostics.service';

// Written while investigating the "rapid sidebar clicking freezes the app" report, before its actual
// root cause (the browser's native link-drag behavior on the sidebar anchors - see shell.html/shell.scss)
// was confirmed with real browser evidence. These tests exercise, with the REAL route tree/guards/lazy
// components (not a trimmed-down stand-in), overlapping navigation, role-guard races, and destroyed-
// component/auth-refresh interactions under rapid navigateByUrl() calls. They found no defect - Angular
// Router already handles this safely on its own - and are kept as genuine, stable regression coverage
// for that guarantee, independent of the drag fix.
//
// Only AuthService is a stub (auth state needs to be synchronous and role-switchable per test; it isn't
// what's under test here) - ResourceService, UserService, BookingService, CalendarService, ApprovalService
// all run for real, backed by HttpTestingController, so a genuine stale-response race would actually have
// somewhere to happen.
describe('rapid sidebar navigation (overlapping, unawaited navigateByUrl calls)', () => {
  let router: Router;
  let httpTesting: HttpTestingController;
  let currentRoles: string[];

  const authServiceStub = {
    isAuthenticated: () => true,
    hasAnyRole: (...roles: string[]) => roles.some((role) => currentRoles.includes(role)),
    get currentUser() {
      return () => ({ userId: 'user-1', email: 'admin@bookspace.test', tenantId: 'tenant-1', roles: currentRoles }) satisfies CurrentUser;
    },
    displayName: () => 'Admin',
    logout: () => undefined,
    currentAccessToken: () => 'fake-access-token',
  };

  // Every route reachable in these tests only ever reads an empty list/paged-result shape (none of them
  // assert on actual DATA - only on which route/component ends up active), so one shape-aware flush
  // covers Dashboard's five concurrent requests, ApprovalQueue's, UserList's, ResourceList's, and
  // Shell's own pending-approval badge fetch without needing per-test URL matching.
  function genericBodyFor(url: string): Record<string, unknown> | unknown[] {
    if (url.includes('/bookings/pending-approval')) {
      return [];
    }
    if (url.includes('/resource-types')) {
      return [];
    }
    // Both /bookings (own bookings, paged or range) and /resources//users (paged lists) share this shape.
    return { items: [], totalCount: 0 };
  }

  function flushAllPending(): void {
    for (const req of httpTesting.match(() => true)) {
      req.flush(genericBodyFor(req.request.url));
    }
  }

  function configure(roles: string[]): void {
    currentRoles = roles;
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: authServiceStub },
      ],
    });
    router = TestBed.inject(Router);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    // Some tests deliberately leave requests from an abandoned navigation unflushed until the assertion
    // itself has run - clean up whatever's left so it doesn't leak into a later test's HttpTestingController.
    for (const req of httpTesting.match(() => true)) {
      req.flush(genericBodyFor(req.request.url));
    }
  });

  it('lets the final route win for Dashboard -> Users -> Resources clicked before any of them settle (TenantAdmin)', async () => {
    configure(['TenantAdmin']);

    const first = router.navigateByUrl('/');
    const second = router.navigateByUrl('/users');
    const third = router.navigateByUrl('/resources');
    flushAllPending();

    const results = await Promise.allSettled([first, second, third]);

    expect(results.every((r) => r.status === 'fulfilled')).toBe(true);
    expect(router.url).toBe('/resources');
  });

  it('lets the final route win for Resources -> Calendar -> My Bookings clicked before any of them settle', async () => {
    configure(['Member']);

    const first = router.navigateByUrl('/resources');
    const second = router.navigateByUrl('/calendar');
    const third = router.navigateByUrl('/bookings');
    flushAllPending();

    const results = await Promise.allSettled([first, second, third]);

    expect(results.every((r) => r.status === 'fulfilled')).toBe(true);
    expect(router.url).toBe('/bookings');
  });

  it('lets a later permitted route win a race against an earlier role-rejected one (Member clicking Users then Resources)', async () => {
    configure(['Member']); // Member cannot see /users - roleGuard redirects it to '/'

    const first = router.navigateByUrl('/');
    const rejected = router.navigateByUrl('/users');
    const permitted = router.navigateByUrl('/resources');
    flushAllPending();

    const results = await Promise.allSettled([first, rejected, permitted]);

    expect(results.every((r) => r.status === 'fulfilled')).toBe(true);
    // The later, permitted click must win over the earlier click's redirect target ('/') - proving the
    // guard's UrlTree redirect doesn't outrace a genuinely newer, valid navigation.
    expect(router.url).toBe('/resources');
  });

  it('does not break when the same route is clicked twice in immediate succession', async () => {
    configure(['TenantAdmin']);
    await router.navigateByUrl('/');
    flushAllPending();

    const first = router.navigateByUrl('/resources');
    const second = router.navigateByUrl('/resources');
    flushAllPending();

    const results = await Promise.allSettled([first, second]);

    expect(results.every((r) => r.status === 'fulfilled')).toBe(true);
    expect(router.url).toBe('/resources');
  });

  // The scenario the task specifically calls out: Dashboard fires several requests, the user navigates
  // away before they resolve, and only THEN do those requests finally settle - on a component Angular has
  // already destroyed. This must not throw, must not corrupt the now-active route's own state (Dashboard's
  // signals are instance-private, so there is no shared field for it to corrupt), and must leave the
  // router in a clean, non-pending state.
  it('does not corrupt the active route when Dashboard requests resolve late, after the user already navigated to Users', async () => {
    configure(['TenantAdmin']);
    // A rendered <router-outlet> (via the harness) is required for DashboardComponent to actually be
    // INSTANTIATED (and therefore for its constructor's .subscribe() calls to actually fire) - a bare
    // router.navigateByUrl() with no outlet anywhere updates the URL/runs guards but never activates a
    // component, so there would be nothing here to leave pending in the first place.
    const harness = await RouterTestingHarness.create('/');
    // Dashboard's requests are now genuinely in flight and deliberately NOT flushed yet.
    expect(httpTesting.match(() => true).length).toBeGreaterThan(0);

    await harness.navigateByUrl('/users');
    harness.detectChanges();

    // Users' own request(s) plus Dashboard's still-unflushed, now-orphaned ones are both outstanding -
    // flushing ALL of them (including onto a component Angular has already destroyed) must not throw.
    expect(() => flushAllPending()).not.toThrow();

    expect(router.url).toBe('/users');
    harness.detectChanges();
    expect(harness.routeNativeElement?.querySelector('app-user-list')).toBeTruthy();
  });

  it('leaves the sidebar/shell usable after 10 rapid, overlapping clicks across every reachable route (TenantAdmin)', async () => {
    configure(['TenantAdmin']);
    const targets = ['/', '/users', '/resources', '/resource-types', '/bookings', '/calendar', '/approvals', '/', '/users', '/resources'];

    const pending = targets.map((url) => router.navigateByUrl(url));
    flushAllPending();
    const results = await Promise.allSettled(pending);

    expect(results.every((r) => r.status === 'fulfilled')).toBe(true);
    expect(router.url).toBe(targets[targets.length - 1]);
    // No navigation left permanently pending - a further, ordinary navigation must still work cleanly.
    flushAllPending();
    const final = await router.navigateByUrl('/calendar');
    flushAllPending();
    expect(final).toBe(true);
    expect(router.url).toBe('/calendar');
  });

  it('renders the actually-final route component under the shell outlet, not a stale one, after overlapping navigation', async () => {
    configure(['TenantAdmin']);
    const harness = await RouterTestingHarness.create();

    const first = router.navigateByUrl('/');
    const second = router.navigateByUrl('/users');
    const third = router.navigateByUrl('/resources');
    flushAllPending();
    await Promise.allSettled([first, second, third]);
    flushAllPending();
    harness.detectChanges();

    expect(router.url).toBe('/resources');
    // ResourceListComponent's own selector - proves the DOM under the outlet matches the route that
    // actually won the race, not Users (which lost) or Dashboard (the harness's initial state).
    expect(harness.routeNativeElement?.querySelector('app-resource-list')).toBeTruthy();
    expect(harness.routeNativeElement?.querySelector('app-user-list')).toBeFalsy();
  });

  // Directly answers the task's "check whether the shell/sidebar DOM is recreated incorrectly" concern:
  // ShellComponent is the PARENT route's own component (see app.routes.ts) - only its CHILD outlet should
  // be torn down and rebuilt as the user navigates between Dashboard/Users/Resources/etc. The sidebar
  // itself, and the anchor elements inside it, must be the literal same DOM nodes before and after.
  it('keeps the exact same sidebar anchor DOM nodes (not recreated copies) across repeated child navigation', async () => {
    configure(['TenantAdmin']);
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();

    const root = harness.routeNativeElement!;
    // Queried against the whole document, not just `root` - RouterTestingHarness's fixture is attached
    // to the real test DOM, and <app-shell> is `root` itself here (the parent route's own activated
    // component), so a query scoped to root's descendants would never see it.
    expect(document.querySelectorAll('app-shell')).toHaveLength(1);
    expect(root.querySelectorAll('.shell-sidebar')).toHaveLength(1);
    expect(root.querySelectorAll('.shell-nav')).toHaveLength(1);
    const resourcesLinkBefore = root.querySelector('a[href="/resources"]');
    expect(resourcesLinkBefore).toBeTruthy();

    for (const url of ['/users', '/resources', '/bookings', '/']) {
      await harness.navigateByUrl(url);
      flushAllPending();
      harness.detectChanges();
    }

    expect(document.querySelectorAll('app-shell')).toHaveLength(1);
    expect(root.querySelectorAll('.shell-sidebar')).toHaveLength(1);
    expect(root.querySelectorAll('.shell-nav')).toHaveLength(1);
    const resourcesLinkAfter = root.querySelector('a[href="/resources"]');
    // Same node, not merely an equal-looking one - proves the sidebar was never torn down and rebuilt.
    expect(resourcesLinkAfter).toBe(resourcesLinkBefore);
    expect(resourcesLinkAfter?.isConnected).toBe(true);
  });

  it('renders exactly one anchor for every sidebar route, never zero or duplicated (TenantAdmin sees every link)', async () => {
    configure(['TenantAdmin']);
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();

    const root = harness.routeNativeElement!;
    const expectedHrefs = ['/', '/resources', '/bookings', '/calendar', '/approvals', '/users', '/resource-types'];
    for (const href of expectedHrefs) {
      expect(root.querySelectorAll(`a.shell-nav__link[href="${href}"]`)).toHaveLength(1);
    }
  });

  // The real runtime trace the task supplied showed two identical GET /bookings/pending-approval
  // requests ~8ms apart on one navigation to /approvals. Traced to source: ShellComponent's OWN
  // constructor (shell.ts) independently calls approvalService.getPendingApprovals() for its sidebar
  // badge count, and ApprovalQueueComponent's constructor (approval-queue.ts) independently calls the
  // SAME method for its own list. Neither call is a leftover/accidental duplicate of the other - they are
  // two different, legitimate features (a badge count vs. a full list) that happen to read the same
  // backend endpoint with no shared cache between them (unlike getResourceTypes(), which IS cached).
  // Landing directly on /approvals as the first navigation of a session (a deep link, or a page reload
  // while already there) constructs Shell and ApprovalQueue in the same navigation, firing both.
  it('explains the duplicate /bookings/pending-approval request: Shell (badge) and ApprovalQueue (list) each fire their own, independent, uncached request when landing directly on /approvals', async () => {
    configure(['TenantAdmin']); // hasAnyRole('Approver','TenantAdmin','SysAdmin') is true, so Shell's badge fetch runs

    const harness = await RouterTestingHarness.create('/approvals');
    harness.detectChanges();

    const pendingApprovalRequests = httpTesting.match((req) => req.url.includes('/bookings/pending-approval'));
    // This is the confirmed, explained behavior - not something this task changes, since both call sites
    // are independently intentional and neither is a bug to "remove." See the comment above.
    expect(pendingApprovalRequests).toHaveLength(2);

    pendingApprovalRequests.forEach((req) => req.flush([]));
    flushAllPending();
  });

  // The contrasting case: navigating to /approvals as a SECOND step (Shell already constructed earlier)
  // must fire only ApprovalQueue's own request - Shell does not re-run its constructor on child
  // navigation (proven by the "keeps the exact same sidebar anchor DOM nodes" test above), so it must not
  // re-fire its badge request either.
  it('fires only one /bookings/pending-approval request when navigating to Approvals as a second step (Shell already constructed)', async () => {
    configure(['TenantAdmin']);
    const harness = await RouterTestingHarness.create('/'); // Shell constructs here, on Dashboard
    flushAllPending();
    harness.detectChanges();

    await harness.navigateByUrl('/approvals');
    harness.detectChanges();

    const pendingApprovalRequests = httpTesting.match((req) => req.url.includes('/bookings/pending-approval'));
    expect(pendingApprovalRequests).toHaveLength(1);
    pendingApprovalRequests.forEach((req) => req.flush([]));
  });
});

// The one scenario the previous describe block's stubbed AuthService can't exercise: what happens to the
// SHARED refresh (refreshInFlight$/navigator.locks - see auth.service.ts) when the component that
// triggered it is destroyed by navigation before the refresh completes. Uses the REAL AuthService and the
// REAL authInterceptor - only the backend is faked via HttpTestingController - so this is the actual
// production auth-refresh machinery under a genuine "navigate away mid-401-retry" condition, not a
// simulation of it.
describe('auth refresh survives the originating component being destroyed mid-navigation', () => {
  const ROLE_CLAIM_TYPE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

  function tokenWithPayload(payload: Record<string, unknown>): string {
    const base64Url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    const header = base64Url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
    const body = base64Url(JSON.stringify(payload));
    return `${header}.${body}.signature-not-verified-client-side`;
  }

  function tenantAdminAccessToken(): string {
    return tokenWithPayload({
      sub: 'user-1',
      email: 'admin@bookspace.test',
      tenant_id: 'tenant-1',
      [ROLE_CLAIM_TYPE]: ['TenantAdmin'],
    });
  }

  const EXPIRED_TOKENS: AuthTokens = {
    accessToken: tenantAdminAccessToken(),
    accessTokenExpiresAtUtc: new Date(Date.now() - 60_000).toISOString(),
    refreshToken: 'refresh-1',
    refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
  };

  let router: Router;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    // Seeded before AuthService is first injected - its `tokens` signal reads TokenStorageService.load()
    // once, synchronously, in its own constructor (same requirement as auth.service.spec.ts).
    TestBed.inject(TokenStorageService).save(EXPIRED_TOKENS, true);
    router = TestBed.inject(Router);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  it('lets Users requests succeed via the shared refresh even though Dashboard (the original 401 trigger) was destroyed before the refresh completed', async () => {
    const harness = await RouterTestingHarness.create('/');

    // Dashboard's several requests plus Shell's own pending-approvals fetch all went out with the
    // already-expired access token - 401 every one of them.
    const initialRequests = httpTesting.match((req) => !req.url.includes('/auth/refresh'));
    expect(initialRequests.length).toBeGreaterThan(0);
    initialRequests.forEach((req) => req.flush('unauthorized', { status: 401, statusText: 'Unauthorized' }));

    // All of those 401s funnel through the same refreshInFlight$ - exactly one real /auth/refresh call,
    // deliberately left unflushed while navigation happens.
    const refreshReq = httpTesting.expectOne((req) => req.url.includes('/auth/refresh'));

    // Navigate away BEFORE the refresh completes. Dashboard is destroyed while its retried requests are
    // still waiting on the shared refresh (nothing in this app cancels a component's HTTP subscription on
    // destroy - see the "does not corrupt the active route..." test above for why that's not itself a bug).
    await harness.navigateByUrl('/users');
    harness.detectChanges();

    // Users' own request also carries the same still-expired token (the refresh hasn't completed yet) and
    // also 401s - it must join the SAME shared refresh, not start a second, redundant one.
    const usersRequest = httpTesting.expectOne((req) => req.url.includes('/users') && !req.url.includes('/auth'));
    usersRequest.flush('unauthorized', { status: 401, statusText: 'Unauthorized' });
    httpTesting.expectNone((req) => req.url.includes('/auth/refresh'));

    // Now let the one shared refresh actually complete.
    const rotated: AuthTokens = {
      accessToken: tenantAdminAccessToken(),
      accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      refreshToken: 'refresh-2',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
    };
    refreshReq.flush(rotated);
    await Promise.resolve();
    await Promise.resolve();

    // Every retried request - Dashboard's orphaned ones AND Users' current one - must now replay with the
    // new token and be able to succeed. This is the actual proof that destroying Dashboard mid-navigation
    // did not leave the shared refresh (or anything waiting on it) stuck.
    const retried = httpTesting.match((req) => !req.url.includes('/auth/refresh'));
    expect(retried.length).toBeGreaterThan(0);
    for (const req of retried) {
      expect(req.request.headers.get('Authorization')).toBe(`Bearer ${rotated.accessToken}`);
      req.flush(req.request.url.includes('/pending-approval') || req.request.url.includes('/resource-types') ? [] : { items: [], totalCount: 0 });
    }

    expect(router.url).toBe('/users');
  });
});

// Gap in every test above: they all trigger navigation via router.navigateByUrl() called directly from
// test code. A real sidebar click never goes through that entry point alone - it goes through
// RouterLink's own native (click) handler on the anchor element first (preventDefault, urlTree
// computation, THEN navigateByUrl). A user report of "Clicks: 1" on the diagnostics indicator right
// before a freeze that also blocks opening DevTools points at something synchronous hanging during the
// FIRST click's own handler chain, before a second click can even be dispatched - navigateByUrl() alone
// cannot exercise that path. These tests dispatch real MouseEvents on the actual rendered <a routerLink>
// elements instead. A genuine synchronous infinite loop here would hang this test process itself (same
// V8 engine, same call stack) exactly as it hangs a real browser tab - that is deliberately the strongest
// possible check available without live browser access.
describe('real click events on the actual sidebar anchors (not programmatic navigateByUrl calls)', () => {
  let httpTesting: HttpTestingController;

  const authServiceStub = {
    isAuthenticated: () => true,
    hasAnyRole: () => true,
    currentUser: () => ({ userId: 'user-1', email: 'admin@bookspace.test', tenantId: 'tenant-1', roles: ['TenantAdmin'] }) satisfies CurrentUser,
    displayName: () => 'Admin',
    logout: () => undefined,
    currentAccessToken: () => 'fake-access-token',
  };

  function genericBodyFor(url: string): Record<string, unknown> | unknown[] {
    if (url.includes('/bookings/pending-approval') || url.includes('/resource-types')) {
      return [];
    }
    return { items: [], totalCount: 0 };
  }

  function flushAllPending(): void {
    for (const req of httpTesting.match(() => true)) {
      req.flush(genericBodyFor(req.request.url));
    }
  }

  function clickAnchor(root: HTMLElement, routerLinkValue: string): void {
    const anchor = [...root.querySelectorAll('a[href]')].find((el) => el.getAttribute('href') === routerLinkValue) as HTMLAnchorElement | undefined;
    if (!anchor) {
      throw new Error(`No sidebar anchor found for routerLink "${routerLinkValue}" - check the sidebar template still renders it.`);
    }
    anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: authServiceStub },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const req of httpTesting.match(() => true)) {
      req.flush(genericBodyFor(req.request.url));
    }
  });

  it('handles two real, back-to-back sidebar clicks (Users then Resources) without hanging', async () => {
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();

    const root = harness.routeNativeElement;
    expect(root).toBeTruthy();

    // Both clicks dispatched synchronously, one immediately after the other - nothing awaited in
    // between - exactly matching a real fast double-click's event dispatch order.
    clickAnchor(root!, '/users');
    clickAnchor(root!, '/resources');

    flushAllPending();
    harness.detectChanges();
    // Let any pending navigation microtasks settle.
    await new Promise((resolve) => setTimeout(resolve, 0));
    flushAllPending();

    expect(TestBed.inject(Router).url).toBe('/resources');
  });

  it('handles ten real, rapid sidebar clicks across every route without hanging', async () => {
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();
    const root = harness.routeNativeElement!;

    const hrefs = ['/users', '/resources', '/resource-types', '/bookings', '/calendar', '/approvals', '/', '/users', '/resources', '/'];
    for (const href of hrefs) {
      clickAnchor(root, href);
      flushAllPending();
    }

    harness.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    flushAllPending();

    expect(TestBed.inject(Router).url).toBe(hrefs[hrefs.length - 1]);
  });

  // The specific gap the freeze investigation's live evidence pointed at: does a real sidebar click get
  // recorded by DiagnosticsService AFTER a navigation has already fully completed (NavigationEnd fired),
  // as opposed to a click fired mid-navigation (already covered by the two tests above)? DiagnosticsService
  // is injected explicitly here (it isn't part of this describe block's providers otherwise) so its real
  // click listener is actually live for this assertion.
  it('records a real sidebar click via DiagnosticsService after the preceding navigation has fully completed', async () => {
    const diagnostics = TestBed.inject(DiagnosticsService);
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();

    await harness.navigateByUrl('/resources');
    flushAllPending();
    harness.detectChanges();
    expect(TestBed.inject(Router).url).toBe('/resources'); // navigation genuinely completed before the click below

    const root = harness.routeNativeElement!;
    clickAnchor(root, '/calendar');

    const clicks = diagnostics.snapshot().events.filter((e) => e.type === 'click');
    expect(clicks).toHaveLength(1);
    expect(clicks[0]).toMatchObject({ targetTag: 'A', route: '/resources' });
  });

  // NOTE on what this does and does not prove: jsdom does not perform real layout, so
  // getBoundingClientRect()/elementFromPoint() are not meaningful here (see diagnostics.service.spec.ts's
  // sidebarSnapshot() tests, which mock those explicitly for that reason) - this is NOT a claim that real
  // browser hit-testing has been verified. What IS meaningful in jsdom is computed CSS style resolution,
  // which does not depend on layout geometry - so this proves that the REAL, actually-rendered
  // ShellComponent's own CSS rules do not themselves declare any pointer-events:none/visibility:hidden/
  // display:none/transform on any sidebar-link ancestor in ordinary (no dialog open) state. Real
  // browser evidence is still required to confirm actual pixel-level hit-testing.
  it('finds no ancestor CSS concerns for any sidebar link in the real, rendered ShellComponent (ordinary state, no dialog open)', async () => {
    const diagnostics = TestBed.inject(DiagnosticsService);
    const harness = await RouterTestingHarness.create('/');
    flushAllPending();
    harness.detectChanges();
    void harness;

    const snapshot = diagnostics.sidebarSnapshot();

    expect(snapshot.links.length).toBeGreaterThan(0);
    for (const link of snapshot.links) {
      expect(link.ancestorConcerns).toEqual([]);
    }
    expect(snapshot.sidebarCount).toBe(1);
    expect(snapshot.appShellCount).toBe(1);
  });
});
