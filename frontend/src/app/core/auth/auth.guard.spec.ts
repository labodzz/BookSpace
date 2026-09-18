import { TestBed } from '@angular/core/testing';
import { Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

describe('authGuard', () => {
  let authService: { isAuthenticated: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    authService = { isAuthenticated: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'welcome', children: [] }]), { provide: AuthService, useValue: authService }],
    });
  });

  function activate(url: string) {
    return TestBed.runInInjectionContext(() => authGuard(null!, { url } as RouterStateSnapshot));
  }

  it('allows activation when the session is still valid', () => {
    authService.isAuthenticated.mockReturnValue(true);

    expect(activate('/bookings')).toBe(true);
  });

  it('redirects to /welcome with a returnUrl when there is no revivable session', () => {
    authService.isAuthenticated.mockReturnValue(false);

    const result = activate('/bookings');
    const router = TestBed.inject(Router);

    expect(router.serializeUrl(result as UrlTree)).toBe('/welcome?returnUrl=%2Fbookings');
  });

  // Every navigation must call isAuthenticated() fresh rather than trusting whatever an earlier
  // navigation observed - proven here by the same guard reaching the opposite conclusion on two calls
  // that differ only in what the (freshly re-evaluated) session state currently is.
  it('re-evaluates session validity on every navigation instead of reusing an earlier result', () => {
    authService.isAuthenticated.mockReturnValue(true);
    expect(activate('/bookings')).toBe(true);

    authService.isAuthenticated.mockReturnValue(false);
    const result = activate('/bookings');

    expect(authService.isAuthenticated).toHaveBeenCalledTimes(2);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toContain('/welcome');
  });
});
