import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ApprovalService } from '../approvals/approval.service';

// The authenticated layout every protected page renders inside of - a sidebar shell (brand, primary
// nav, user/logout) wrapping a <router-outlet>.
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class ShellComponent {
  protected readonly authService = inject(AuthService);
  private readonly approvalService = inject(ApprovalService);
  private readonly router = inject(Router);

  protected readonly isApprover = this.authService.hasAnyRole('Approver', 'TenantAdmin', 'SysAdmin');
  protected readonly pendingApprovalCount = signal(0);

  constructor() {
    if (this.isApprover) {
      this.approvalService.getPendingApprovals().subscribe((approvals) => this.pendingApprovalCount.set(approvals.length));
    }
  }

  logout(): void {
    this.authService.logout();
    this.router.navigate(['/welcome']);
  }
}
