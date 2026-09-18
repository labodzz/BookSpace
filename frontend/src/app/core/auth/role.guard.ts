import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

// Gates a route to one of the given roles. Assumes authGuard has already run (this only ever narrows
// further, it doesn't check isAuthenticated itself) - an authenticated user missing every listed role
// is sent to the dashboard rather than the login screen, since they don't need to sign in again, they
// just can't see this particular page.
export const roleGuard = (...roles: string[]): CanActivateFn => {
  return () => {
    const authService = inject(AuthService);
    const router = inject(Router);

    return authService.hasAnyRole(...roles) ? true : router.createUrlTree(['/']);
  };
};
