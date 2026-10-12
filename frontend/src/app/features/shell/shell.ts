import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ThemeToggleComponent } from '../../core/theme/theme-toggle/theme-toggle';
import { AiAssistantWidgetComponent } from '../ai-assistant/ai-assistant-widget/ai-assistant-widget';
import { ApprovalService } from '../approvals/approval.service';

// The authenticated layout every protected page renders inside of - a sidebar shell (brand, primary
// nav, user/logout) wrapping a <router-outlet>. Also the one place the floating AI assistant widget is
// rendered (see shell.html) - this component persists across every child-route navigation (the router
// only ever swaps <router-outlet>'s content), which is what makes the assistant's conversation survive
// navigating between tenant pages and ensures exactly one instance ever exists, without the widget itself
// needing to know anything about routing.
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeToggleComponent, AiAssistantWidgetComponent],
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
  // bookings/calendar/approvals/users) below, AND never the AI booking assistant (see shell.html): a
  // SysAdmin is not a tenant member and POST /api/ai-bookings/propose rejects them the same way
  // POST /api/bookings itself does (TenantMembershipGuard) - showing the launcher here would offer a
  // capability this role can never actually use. See docs/tenant-onboarding.md, "SysAdmin and tenant
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
