import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { environment } from '../environments/environment';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth.service';
import { TokenStorageService } from './core/auth/token-storage.service';
import { DIAGNOSTICS_ENABLED, DiagnosticsService } from './core/diagnostics/diagnostics.service';
import { diagnosticsInterceptor } from './core/diagnostics/diagnostics.interceptor';
import { authInterceptor } from './core/http/auth.interceptor';
import { AcceptInvitationComponent } from './features/auth/accept-invitation/accept-invitation';
import { LoginComponent } from './features/auth/login/login';

@Component({
  selector: 'test-invitation-shell',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
class TestShell {}

@Component({ selector: 'test-invitation-dashboard', template: 'Dashboard' })
class TestDashboard {}

describe('Invitation routing and session handoff', () => {
  const token = 'invitation-secret+/=';
  const url = `/accept-invitation?token=${encodeURIComponent(token)}`;
  let http: HttpTestingController;
  let auth: AuthService;
  let router: Router;
  let harness: RouterTestingHarness;

  async function setup(signedIn = false): Promise<void> {
    localStorage.clear();
    sessionStorage.clear();
    // Exercise the production route definitions and guards. Only replace the protected shell and
    // dashboard views so unrelated dashboard API calls cannot influence these routing tests.
    const testRoutes = routes.map((route) =>
      route.path === ''
        ? {
            ...route,
            loadComponent: () => Promise.resolve(TestShell),
            children: route.children?.map((child) => ({
              ...child,
              loadComponent: () => Promise.resolve(TestDashboard),
            })),
          }
        : route,
    );
    TestBed.configureTestingModule({
      providers: [
        provideRouter(testRoutes),
        provideHttpClient(withInterceptors([authInterceptor, diagnosticsInterceptor])),
        provideHttpClientTesting(),
        { provide: DIAGNOSTICS_ENABLED, useValue: true },
      ],
    });
    if (signedIn) {
      const payload = btoa(
        JSON.stringify({
          sub: 'sysadmin',
          email: 'admin@bookspace.test',
          'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': 'SysAdmin',
        }),
      );
      TestBed.inject(TokenStorageService).save(
        {
          accessToken: `header.${payload}.signature`,
          accessTokenExpiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
          refreshToken: 'sysadmin-refresh',
          refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
        },
        true,
      );
    }
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  }

  afterEach(() => {
    http?.verify();
    localStorage.clear();
    sessionStorage.clear();
    (auth as unknown as { broadcastChannel: BroadcastChannel | null })?.broadcastChannel?.close();
    vi.restoreAllMocks();
  });

  function root(): HTMLElement {
    return harness.routeNativeElement!;
  }

  function submit(): void {
    for (const id of ['password', 'confirm-password']) {
      const input = root().querySelector(`#${id}`) as HTMLInputElement;
      input.value = 'Correct-Horse-9!';
      input.dispatchEvent(new Event('input'));
    }
    root().querySelector('form')!.dispatchEvent(new Event('submit'));
    harness.detectChanges();
  }

  it('opens the public invitation with its token and form when signed out', async () => {
    await setup();
    await harness.navigateByUrl(url, AcceptInvitationComponent);
    expect(router.url).toBe(url);
    expect(root().querySelector('#password')).not.toBeNull();
  });

  it('keeps a SysAdmin on the invitation with an explicit logout action and no password form', async () => {
    await setup(true);
    const logout = vi.spyOn(auth, 'logout');
    const component = await harness.navigateByUrl(url, AcceptInvitationComponent);
    expect(router.url).toBe(url);
    expect(root().textContent).toContain('admin@bookspace.test');
    expect(root().textContent).toContain('Sign out and continue');
    expect(root().querySelector('form')).toBeNull();
    expect(logout).not.toHaveBeenCalled();
    component.submit();
    http.expectNone(`${environment.apiUrl}/auth/accept-invitation`);
  });

  for (const fails of [false, true]) {
    it(`logs out, clears both stores and preserves the token when backend logout ${fails ? 'fails' : 'succeeds'}`, async () => {
      await setup(true);
      await harness.navigateByUrl(url, AcceptInvitationComponent);
      const logout = vi.spyOn(auth, 'logout');
      (root().querySelector('button.accept-invitation-submit') as HTMLButtonElement).click();
      const req = http.expectOne(`${environment.apiUrl}/auth/logout`);
      expect(req.request.body).toEqual({ refreshToken: 'sysadmin-refresh' });
      expect(logout).toHaveBeenCalledOnce();
      expect(auth.isAuthenticated()).toBe(false);
      expect(auth.currentAccessToken()).toBeNull();
      expect(TestBed.inject(TokenStorageService).load()).toBeNull();
      for (const store of [localStorage, sessionStorage]) {
        expect(store.getItem('bookspace.accessToken')).toBeNull();
        expect(store.getItem('bookspace.refreshToken')).toBeNull();
      }
      if (fails) req.flush(null, { status: 500, statusText: 'Server Error' });
      else req.flush(null, { status: 204, statusText: 'No Content' });
      harness.detectChanges();
      expect(router.url).toBe(url);
      expect(root().querySelector('#password')).not.toBeNull();
      submit();
      const accept = http.expectOne(`${environment.apiUrl}/auth/accept-invitation`);
      expect(accept.request.body.token).toBe(token);
      expect(accept.request.headers.has('Authorization')).toBe(false);
      accept.flush(null, { status: 204, statusText: 'No Content' });
      await harness.fixture.whenStable();
      harness.detectChanges();
      expect(router.url).toBe('/login?invitationAccepted=true');
      expect(root().textContent).toContain('Your password is set');
      expect(auth.isAuthenticated()).toBe(false);
    });
  }

  it('preserves the invitation when logout is broadcast by another tab', async () => {
    await setup(true);
    await harness.navigateByUrl(url, AcceptInvitationComponent);
    const channel = (auth as unknown as { broadcastChannel: BroadcastChannel }).broadcastChannel;
    channel.onmessage!(new MessageEvent('message', { data: { type: 'logged-out' } }));
    harness.detectChanges();
    expect(router.url).toBe(url);
    expect(auth.isAuthenticated()).toBe(false);
    expect(root().querySelector('#password')).not.toBeNull();
  });

  it('shows the missing-token error even for an authenticated browser', async () => {
    await setup(true);
    await harness.navigateByUrl('/accept-invitation', AcceptInvitationComponent);
    expect(root().textContent).toContain("This invitation link isn't valid");
    expect(root().querySelector('form')).toBeNull();
  });

  it.each(['invalid', 'expired', 'used'])(
    'retains the generic backend error for a %s token',
    async (value) => {
      await setup();
      await harness.navigateByUrl(`/accept-invitation?token=${value}`, AcceptInvitationComponent);
      submit();
      http
        .expectOne(`${environment.apiUrl}/auth/accept-invitation`)
        .flush(
          { title: 'Authentication failed.', detail: 'This invitation link is no longer valid.' },
          { status: 401, statusText: 'Unauthorized' },
        );
      await harness.fixture.whenStable();
      harness.detectChanges();
      expect(root().textContent).toContain('This invitation link is no longer valid.');
      expect(root().querySelector('form')).not.toBeNull();
      expect(router.url).toContain('/accept-invitation');
    },
  );

  it('still redirects an authenticated visitor away from login to the dashboard', async () => {
    await setup(true);
    await harness.navigateByUrl('/login');
    expect(router.url).toBe('/');
    expect(root().textContent).toContain('Dashboard');
  });

  it('treats the invitation API as token-based even if another session is present', async () => {
    await setup(true);
    const failed = vi.fn();
    auth.acceptInvitation(token, 'Correct-Horse-9!').subscribe({ error: failed });
    const req = http.expectOne(`${environment.apiUrl}/auth/accept-invitation`);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush(
      { detail: 'This invitation link is no longer valid.' },
      { status: 401, statusText: 'Unauthorized' },
    );
    expect(failed).toHaveBeenCalledOnce();
    http.expectNone(`${environment.apiUrl}/auth/refresh`);
    expect(auth.isAuthenticated()).toBe(true);
  });

  it('keeps login public and protected routes gated for a signed-out browser', async () => {
    await setup();
    await harness.navigateByUrl('/login', LoginComponent);
    expect(root().querySelector('#email')).not.toBeNull();
    await harness.navigateByUrl('/resources');
    expect(router.url).toBe('/welcome?returnUrl=%2Fresources');
  });

  it('does not put the invitation token in diagnostics, console output or storage', async () => {
    await setup(true);
    const spies = ['log', 'info', 'warn', 'error', 'debug'].map((method) =>
      vi.spyOn(console, method as 'log'),
    );
    await harness.navigateByUrl(url, AcceptInvitationComponent);
    (root().querySelector('button.accept-invitation-submit') as HTMLButtonElement).click();
    http
      .expectOne(`${environment.apiUrl}/auth/logout`)
      .flush(null, { status: 204, statusText: 'No Content' });
    harness.detectChanges();
    submit();
    http
      .expectOne(`${environment.apiUrl}/auth/accept-invitation`)
      .flush(null, { status: 204, statusText: 'No Content' });
    await harness.fixture.whenStable();
    const snapshot = JSON.stringify(TestBed.inject(DiagnosticsService).snapshot());
    expect(snapshot).not.toContain(token);
    expect(snapshot).not.toContain(encodeURIComponent(token));
    expect(JSON.stringify(spies.map((spy) => spy.mock.calls))).not.toContain(token);
    for (const store of [localStorage, sessionStorage]) {
      expect(JSON.stringify(Object.entries(store))).not.toContain(token);
    }
  });
});
