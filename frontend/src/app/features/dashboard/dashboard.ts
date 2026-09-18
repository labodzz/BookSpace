import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { forkJoin } from 'rxjs';
import { NotificationService } from '../../core/notifications/notification.service';
import { AuthService } from '../../core/auth/auth.service';
import { ApprovalService } from '../approvals/approval.service';
import { PendingApproval } from '../approvals/approval.models';
import { BookingService } from '../bookings/booking.service';
import { OwnBooking } from '../bookings/booking.models';
import { toLocalDateTime } from '../bookings/local-time.util';
import { ResourceService } from '../resources/resource.service';
import { ResourceSummary } from '../resources/resource.models';

const UPCOMING_HORIZON_DAYS = 90;
const UPCOMING_PREVIEW_COUNT = 5;
const PENDING_PREVIEW_COUNT = 3;
const AVAILABLE_RESOURCES_PREVIEW_COUNT = 4;

// The real landing page: today's snapshot built entirely from the same services every other WP-7
// screen uses - no separate/fake summary endpoint, no hardcoded numbers.
@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent {
  protected readonly authService = inject(AuthService);
  private readonly bookingService = inject(BookingService);
  private readonly resourceService = inject(ResourceService);
  private readonly approvalService = inject(ApprovalService);
  private readonly notificationService = inject(NotificationService);

  protected readonly isApprover = this.authService.hasAnyRole('Approver', 'TenantAdmin', 'SysAdmin');
  protected readonly today = DateTime.now().toFormat('cccc, LLLL d');

  protected readonly loading = signal(true);
  protected readonly upcomingBookings = signal<OwnBooking[]>([]);
  protected readonly upcomingCount = signal(0);
  protected readonly upcomingThisWeekCount = signal(0);
  protected readonly resourceNames = signal<Record<string, string>>({});
  protected readonly activeResourceCount = signal(0);
  protected readonly totalBookingCount = signal(0);
  protected readonly pendingApprovals = signal<PendingApproval[]>([]);
  protected readonly pendingApprovalCount = signal(0);
  protected readonly availableResources = signal<ResourceSummary[]>([]);
  protected readonly decidingBookingId = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected resourceNameFor(resourceId: string): string {
    return this.resourceNames()[resourceId] ?? 'Loading…';
  }

  protected typeName(resourceTypeId: string): string {
    return this.resourceService.resourceTypeName(resourceTypeId);
  }

  protected formatRange(startUtc: string, endUtc: string): string {
    const start = toLocalDateTime(startUtc);
    const end = toLocalDateTime(endUtc);
    const prefix = start.hasSame(DateTime.now(), 'day') ? 'Today' : start.toFormat('ccc, LLL d');
    return `${prefix}, ${start.toFormat('HH:mm')} – ${end.toFormat('HH:mm')}`;
  }

  protected decide(approval: PendingApproval, decision: 'approve' | 'reject'): void {
    this.decidingBookingId.set(approval.bookingId);
    const request$ = decision === 'approve' ? this.approvalService.approve(approval.bookingId) : this.approvalService.reject(approval.bookingId);

    request$.subscribe({
      next: () => {
        this.decidingBookingId.set(null);
        this.notificationService.showSuccess(decision === 'approve' ? 'Booking approved.' : 'Booking rejected.');
        this.pendingApprovals.update((approvals) => approvals.filter((item) => item.bookingId !== approval.bookingId));
        this.pendingApprovalCount.update((count) => Math.max(0, count - 1));
      },
      error: () => this.decidingBookingId.set(null),
    });
  }

  private load(): void {
    const now = DateTime.now().toUTC().toISO()!;
    const horizon = DateTime.now().plus({ days: UPCOMING_HORIZON_DAYS }).toUTC().toISO()!;

    this.bookingService.getOwnBookingsInRange(now, horizon).subscribe((bookings) => {
      const upcoming = bookings
        .filter((booking) => booking.status === 'Pending' || booking.status === 'Confirmed')
        .sort((a, b) => a.startUtc.localeCompare(b.startUtc));
      const weekFromNow = DateTime.now().plus({ days: 7 });

      this.upcomingCount.set(upcoming.length);
      this.upcomingThisWeekCount.set(upcoming.filter((booking) => toLocalDateTime(booking.startUtc) <= weekFromNow).length);

      const preview = upcoming.slice(0, UPCOMING_PREVIEW_COUNT);
      this.upcomingBookings.set(preview);
      this.loadResourceNames(preview);
    });

    this.bookingService.getOwnBookings(1, 1).subscribe((result) => this.totalBookingCount.set(result.totalCount));
    this.resourceService.getResources(1, AVAILABLE_RESOURCES_PREVIEW_COUNT, undefined, 'Active').subscribe((result) => {
      this.activeResourceCount.set(result.totalCount);
      this.availableResources.set(result.items);
    });
    this.resourceService.getResourceTypes().subscribe();

    if (this.isApprover) {
      this.approvalService.getPendingApprovals().subscribe((approvals) => {
        this.pendingApprovalCount.set(approvals.length);
        const preview = approvals.slice(0, PENDING_PREVIEW_COUNT);
        this.pendingApprovals.set(preview);
        this.loadResourceNames(preview.map((approval) => ({ resourceId: approval.resourceId })));
      });
    }

    this.loading.set(false);
  }

  private loadResourceNames(items: { resourceId: string }[]): void {
    const known = this.resourceNames();
    const missingIds = [...new Set(items.map((item) => item.resourceId))].filter((id) => !(id in known));
    if (missingIds.length === 0) {
      return;
    }

    forkJoin(missingIds.map((id) => this.resourceService.getResourceCached(id))).subscribe({
      next: (resources) => {
        const next = { ...this.resourceNames() };
        resources.forEach((resource) => (next[resource.id] = resource.name));
        this.resourceNames.set(next);
      },
      error: () => {
        const next = { ...this.resourceNames() };
        missingIds.forEach((id) => (next[id] = 'Unknown resource'));
        this.resourceNames.set(next);
      },
    });
  }
}
