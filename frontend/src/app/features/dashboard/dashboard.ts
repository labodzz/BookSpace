import { Component, inject } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';

// Placeholder landing page proving a signed-in user reaches a real protected area - the booking
// dashboard itself is separate follow-up work.
@Component({
  selector: 'app-dashboard',
  imports: [],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent {
  protected readonly authService = inject(AuthService);
}
