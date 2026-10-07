import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ThemeToggleComponent } from '../../core/theme/theme-toggle/theme-toggle';
import { ApprovalService } from '../approvals/approval.service';

// The authenticated layout every protected page renders inside of - a sidebar shell (brand, primary
// nav, user/logout) wrapping a <router-outlet>.
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeToggleComponent],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class ShellComponent {
  protected readonly authService = inject(AuthService);
  private readonly approvalService = inject(ApprovalService);
  private readonly router = inject(Router);

  // Never SysAdmin: GET /bookings/pending-approval is TenantAdmin-only besides Approver itself (a global
  // SysAdmin is not a tenant member and gets 403 there now - see ApprovalAuthorization).
  protected readonly isApprover = this.authService.hasAnyRole('Approver', 'TenantAdmin');
  // The global platform admin - sees tenant-lifecycle nav instead of any tenant-business nav (resources/
  // bookings/calendar/approvals/users) below. See docs/tenant-onboarding.md, "SysAdmin and tenant
  // business data".
  protected readonly isSysAdmin = this.authService.hasAnyRole('SysAdmin');
  protected readonly pendingApprovalCount = signal(0);

  constructor() {
    if (this.isApprover) {
      this.approvalService.getPendingApprovals().subscribe({
        next: (approvals) => this.pendingApprovalCount.set(approvals.length),
        // Leaves the nav badge at its initial 0 rather than throwing an unhandled RxJS error - the
        // global error interceptor's toast is the user-facing feedback for the failed request itself.
        error: () => undefined,
      });
    }
  }

  logout(): void {
    this.authService.logout();
    this.router.navigate(['/welcome']);
  }
}
