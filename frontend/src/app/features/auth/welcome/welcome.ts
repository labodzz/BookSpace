import { Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

// The first screen an unauthenticated visitor sees - authGuard sends anyone without a revivable
// session here (see core/auth/auth.guard.ts), carrying returnUrl forward so it can be handed off to
// the actual login form once "Log in" is clicked.
@Component({
  selector: 'app-welcome',
  imports: [RouterLink],
  templateUrl: './welcome.html',
  styleUrl: './welcome.scss',
})
export class WelcomeComponent {
  private readonly route = inject(ActivatedRoute);
  protected readonly returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
}
