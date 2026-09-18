import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

// Blocks any route it's attached to unless there's a still-revivable session, redirecting to /welcome
// with a returnUrl so the user lands back where they were headed after signing in (the welcome screen
// forwards returnUrl to /login when "Log in" is clicked).
export const authGuard: CanActivateFn = (_route, state) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  if (authService.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree(['/welcome'], { queryParams: { returnUrl: state.url } });
};
