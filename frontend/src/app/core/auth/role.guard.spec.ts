import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { roleGuard } from './role.guard';
import { AuthService } from './auth.service';

describe('roleGuard', () => {
  let authService: { hasAnyRole: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    authService = { hasAnyRole: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: authService }],
    });
  });

  it('allows activation when the user has one of the required roles', () => {
    authService.hasAnyRole.mockReturnValue(true);

    const result = TestBed.runInInjectionContext(() => roleGuard('Approver', 'TenantAdmin')(null!, null!));

    expect(result).toBe(true);
    expect(authService.hasAnyRole).toHaveBeenCalledWith('Approver', 'TenantAdmin');
  });

  it('redirects to the dashboard, not the login screen, when the user lacks every required role', () => {
    authService.hasAnyRole.mockReturnValue(false);

    const result = TestBed.runInInjectionContext(() => roleGuard('Approver')(null!, null!));
    const router = TestBed.inject(Router);

    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });
});
