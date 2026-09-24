import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { DateTime } from 'luxon';
import { forkJoin } from 'rxjs';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { CalendarService } from '../../calendar/calendar.service';
import { ResourceService } from '../../resources/resource.service';
import { ApprovalService } from '../approval.service';
import { PendingApproval } from '../approval.models';
import { DualZoneRange, detectViewerTimeZone, formatDualZoneRange } from '../../bookings/local-time.util';

type Decision = 'approve' | 'reject';

interface ApprovalGroup {
  seriesId: string | null;
  items: PendingApproval[];
}

// The approval queue for Approver/TenantAdmin/SysAdmin. GetPendingApprovalsResponseItem only carries a
// requester UserId, not a name or email - there's no endpoint an Approver can call to resolve that (GET
// /users is TenantAdmin/SysAdmin-only and isn't a lookup-by-id anyway), so the requester is shown by id
// rather than inventing a name the API doesn't provide.
//
// Recurring series show up as N separate pending occurrences - a series of, say, 100 weekly occurrences
// would otherwise force an approver to approve one at a time. Occurrences sharing a seriesId are grouped
// with a single "approve all" action (backed by ApproveBookingRequest.approveRemainingSeries, one API
// call), while each occurrence keeps its own Approve/Reject so a specific one can still be decided
// individually without touching the rest of the series.
@Component({
  selector: 'app-approval-queue',
  imports: [],
  templateUrl: './approval-queue.html',
  styleUrl: './approval-queue.scss',
})
export class ApprovalQueueComponent {
  private readonly approvalService = inject(ApprovalService);
  private readonly resourceService = inject(ResourceService);
  private readonly calendarService = inject(CalendarService);
  private readonly notificationService = inject(NotificationService);

  protected readonly approvals = signal<PendingApproval[]>([]);
  protected readonly resourceNames = signal<Record<string, string>>({});
  protected readonly notes = signal<Record<string, string>>({});
  protected readonly rowErrors = signal<Record<string, string>>({});
  protected readonly submittingIds = signal<Set<string>>(new Set());

  protected readonly groupNotes = signal<Record<string, string>>({});
  protected readonly groupErrors = signal<Record<string, string>>({});
  protected readonly submittingGroupIds = signal<Set<string>>(new Set());

  protected readonly viewerZoneId = detectViewerTimeZone();

  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);

  // Occurrences sharing a seriesId are grouped together (in their existing, already-StartUtc-sorted
  // order); a series with only one currently-pending occurrence isn't worth a "group" treatment, so it's
  // treated the same as a plain one-off booking (seriesId: null groups, and singleton series groups, both
  // render as a plain row).
  protected readonly groups = computed<ApprovalGroup[]>(() => {
    const bySeries = new Map<string, PendingApproval[]>();
    const standalone: PendingApproval[] = [];

    for (const approval of this.approvals()) {
      if (!approval.seriesId) {
        standalone.push(approval);
        continue;
      }
      const existing = bySeries.get(approval.seriesId);
      if (existing) {
        existing.push(approval);
      } else {
        bySeries.set(approval.seriesId, [approval]);
      }
    }

    const groups: ApprovalGroup[] = [];
    for (const [seriesId, items] of bySeries) {
      groups.push(items.length > 1 ? { seriesId, items } : { seriesId: null, items });
    }
    for (const approval of standalone) {
      groups.push({ seriesId: null, items: [approval] });
    }

    return groups.sort((a, b) => a.items[0].startUtc.localeCompare(b.items[0].startUtc));
  });

  constructor() {
    this.load();
  }

  protected noteFor(bookingId: string): string {
    return this.notes()[bookingId] ?? '';
  }

  protected setNote(bookingId: string, value: string): void {
    this.notes.update((notes) => ({ ...notes, [bookingId]: value }));
  }

  protected groupNoteFor(seriesId: string): string {
    return this.groupNotes()[seriesId] ?? '';
  }

  protected setGroupNote(seriesId: string, value: string): void {
    this.groupNotes.update((notes) => ({ ...notes, [seriesId]: value }));
  }

  protected resourceNameFor(resourceId: string): string {
    return this.resourceNames()[resourceId] ?? 'Loading…';
  }

  protected isSubmitting(bookingId: string): boolean {
    return this.submittingIds().has(bookingId);
  }

  protected isGroupSubmitting(seriesId: string): boolean {
    return this.submittingGroupIds().has(seriesId);
  }

  // Resource-local time is primary (an approver cares about the resource's own business hours), the
  // viewer's own local time is a secondary line shown only when it differs. alwaysShowDate: true because
  // this is a flat queue spanning many different days with no other day-grouping context - same
  // reasoning as My Bookings (see my-bookings.ts's own comment on its own range()).
  protected range(approval: PendingApproval): DualZoneRange {
    return formatDualZoneRange(approval.startUtc, approval.endUtc, approval.timeZoneId, this.viewerZoneId, { alwaysShowDate: true });
  }

  protected formatExpiry(expiresAtUtc: string): string {
    const expires = DateTime.fromISO(expiresAtUtc);
    return expires < DateTime.now() ? 'Expired' : `Expires ${expires.toRelative()}`;
  }

  protected retry(): void {
    this.load();
  }

  protected decide(approval: PendingApproval, decision: Decision): void {
    this.rowErrors.update((errors) => {
      const next = { ...errors };
      delete next[approval.bookingId];
      return next;
    });
    this.submittingIds.update((ids) => new Set(ids).add(approval.bookingId));

    const note = this.noteFor(approval.bookingId) || undefined;
    const request$ =
      decision === 'approve' ? this.approvalService.approve(approval.bookingId, note) : this.approvalService.reject(approval.bookingId, note);

    request$.subscribe({
      next: () => {
        this.calendarService.invalidate();
        this.notificationService.showSuccess(decision === 'approve' ? 'Booking approved.' : 'Booking rejected.');
        this.stopSubmitting(approval.bookingId);
        this.load();
      },
      error: (error: unknown) => {
        this.stopSubmitting(approval.bookingId);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.rowErrors.update((errors) => ({ ...errors, [approval.bookingId]: apiError.detail ?? apiError.title }));
      },
    });
  }

  // Approves the whole group in a single API call. A sibling occurrence that no longer passes the
  // server's eligibility re-check (a blackout added since, capacity changed) is never force-approved -
  // it stays Pending and comes back in the response as a conflict, so it's still there afterwards for
  // the approver to look at individually (including rejecting just that one).
  protected approveGroup(group: ApprovalGroup): void {
    const seriesId = group.seriesId;
    if (!seriesId) {
      return;
    }

    this.groupErrors.update((errors) => {
      const next = { ...errors };
      delete next[seriesId];
      return next;
    });
    this.submittingGroupIds.update((ids) => new Set(ids).add(seriesId));

    const primary = group.items[0];
    const note = this.groupNoteFor(seriesId) || undefined;

    this.approvalService.approve(primary.bookingId, note, true).subscribe({
      next: (response) => {
        this.calendarService.invalidate();
        this.stopSubmittingGroup(seriesId);

        const approvedCount = 1 + response.cascadedApprovedOccurrenceIds.length;
        const conflictCount = response.cascadedConflicts.length;
        this.notificationService.showSuccess(
          conflictCount === 0
            ? `Approved all ${approvedCount} in the series.`
            : `Approved ${approvedCount} in the series; ${conflictCount} could not be approved and remain pending.`,
        );
        this.load();
      },
      error: (error: unknown) => {
        this.stopSubmittingGroup(seriesId);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.groupErrors.update((errors) => ({ ...errors, [seriesId]: apiError.detail ?? apiError.title }));
      },
    });
  }

  private stopSubmitting(bookingId: string): void {
    this.submittingIds.update((ids) => {
      const next = new Set(ids);
      next.delete(bookingId);
      return next;
    });
  }

  private stopSubmittingGroup(seriesId: string): void {
    this.submittingGroupIds.update((ids) => {
      const next = new Set(ids);
      next.delete(seriesId);
      return next;
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.approvalService.getPendingApprovals().subscribe({
      next: (approvals) => {
        this.approvals.set(approvals);
        this.loading.set(false);
        this.loadResourceNames(approvals);
      },
      error: (error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.loading.set(false);
      },
    });
  }

  private loadResourceNames(approvals: PendingApproval[]): void {
    const known = this.resourceNames();
    const missingIds = [...new Set(approvals.map((approval) => approval.resourceId))].filter((id) => !(id in known));
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
