import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DateTime } from 'luxon';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { parseStrictLocalDateTime } from '../../bookings/local-time.util';
import { NotificationService } from '../../../core/notifications/notification.service';
import { AvailabilityRule, BlackoutPeriod, DayOfWeek, ResourceSummary } from '../resource.models';
import { ResourceService } from '../resource.service';
import { resolveLuxonZone } from '../timezone.util';

// Displayed Monday-first (the app's own weekly-calendar convention - see calendar.html's weekday
// labels), independent of System.DayOfWeek's Sunday-first wire order.
const DAY_DISPLAY_ORDER: readonly DayOfWeek[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

type DeleteTarget = { kind: 'rule'; rule: AvailabilityRule } | { kind: 'blackout'; blackout: BlackoutPeriod };
type BlackoutDialogState = { mode: 'create' } | { mode: 'edit'; blackout: BlackoutPeriod };
type BlackoutTemporalStatus = 'upcoming' | 'active' | 'past';

// Matches the `${date}T${time}` shape parseStrictLocalDateTime expects (no seconds, no offset) - see
// local-time.util.ts.
function toLocalDateTimeString(date: string, time: string): string {
  return `${date}T${time}`;
}

@Component({
  selector: 'app-resource-availability-manage',
  imports: [RouterLink, FormsModule],
  templateUrl: './resource-availability-manage.html',
  styleUrl: './resource-availability-manage.scss',
})
export class ResourceAvailabilityManageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly resourceService = inject(ResourceService);
  private readonly notificationService = inject(NotificationService);

  private readonly resourceId = this.route.snapshot.paramMap.get('id')!;

  protected readonly resource = signal<ResourceSummary | null>(null);
  protected readonly resourceLoading = signal(true);
  protected readonly resourceError = signal<ApiError | null>(null);
  protected readonly resourceNotFound = computed(() => this.resourceError()?.status === 404);
  protected readonly isArchived = computed(() => this.resource()?.status === 'Archived');
  protected readonly zone = computed(() => resolveLuxonZone(this.resource()?.timeZoneId ?? 'UTC'));

  protected readonly rules = signal<AvailabilityRule[]>([]);
  protected readonly rulesLoading = signal(true);
  protected readonly rulesError = signal<ApiError | null>(null);
  protected readonly sortedRules = computed(() => {
    const dayIndex = (day: DayOfWeek) => DAY_DISPLAY_ORDER.indexOf(day);
    return [...this.rules()].sort((a, b) => dayIndex(a.dayOfWeek) - dayIndex(b.dayOfWeek) || a.startTime.localeCompare(b.startTime));
  });

  protected readonly newRuleDayOfWeek = signal<DayOfWeek>('Monday');
  protected readonly newRuleStartTime = signal('09:00');
  protected readonly newRuleEndTime = signal('17:00');
  protected readonly creatingRule = signal(false);
  protected readonly ruleFormError = signal<string | null>(null);
  protected readonly ruleFieldErrors = signal<Record<string, string[]>>({});
  protected readonly ruleDayFieldErrors = computed(() => this.ruleFieldErrors()['DayOfWeek'] ?? []);
  protected readonly ruleTimeFieldErrors = computed(() => [
    ...(this.ruleFieldErrors()['StartTime'] ?? []),
    ...(this.ruleFieldErrors()['EndTime'] ?? []),
  ]);

  protected readonly blackouts = signal<BlackoutPeriod[]>([]);
  protected readonly blackoutsLoading = signal(true);
  protected readonly blackoutsError = signal<ApiError | null>(null);
  protected readonly sortedBlackouts = computed(() =>
    [...this.blackouts()].sort((a, b) => DateTime.fromISO(a.startUtc).toMillis() - DateTime.fromISO(b.startUtc).toMillis()),
  );

  protected readonly blackoutDialog = signal<BlackoutDialogState | null>(null);
  protected readonly blackoutStartDate = signal('');
  protected readonly blackoutStartTime = signal('');
  protected readonly blackoutEndDate = signal('');
  protected readonly blackoutEndTime = signal('');
  protected readonly blackoutReason = signal('');
  protected readonly savingBlackout = signal(false);
  protected readonly blackoutFormError = signal<string | null>(null);
  protected readonly blackoutFieldErrors = signal<Record<string, string[]>>({});
  protected readonly blackoutReasonFieldErrors = computed(() => this.blackoutFieldErrors()['Reason'] ?? []);

  // Shared by both lists - only one delete confirmation can be open at a time.
  protected readonly deleteConfirmation = signal<DeleteTarget | null>(null);
  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  // Set only right after a create/update whose response reported at least one conflicting booking -
  // deliberately NOT a toast (which auto-dismisses in a few seconds); this needs to stay visible until
  // the admin dismisses it themselves, since it is reporting something that did NOT happen (those
  // bookings were not cancelled) rather than confirming something that did.
  protected readonly conflictWarning = signal<{ count: number; ids: string[] } | null>(null);

  constructor() {
    this.loadResource();
  }

  protected retryLoadResource(): void {
    this.loadResource();
  }

  protected retryLoadRules(): void {
    this.loadRules();
  }

  protected retryLoadBlackouts(): void {
    this.loadBlackouts();
  }

  protected dismissConflictWarning(): void {
    this.conflictWarning.set(null);
  }

  // --- Availability rules ---

  protected formatTime(time: string): string {
    return time.slice(0, 5); // "09:00:00" -> "09:00"
  }

  protected submitNewRule(): void {
    // The disabled attribute on the submit button already covers a real click, but this guards the
    // (ngSubmit) path itself too (e.g. pressing Enter again before the response for the first request
    // has come back) - the same explicit check the blackout dialog and delete confirmation use below.
    if (this.creatingRule()) {
      return;
    }

    this.ruleFormError.set(null);
    this.ruleFieldErrors.set({});

    if (this.newRuleEndTime() <= this.newRuleStartTime()) {
      this.ruleFormError.set('End time must be after the start time.');
      return;
    }

    this.creatingRule.set(true);
    this.resourceService
      .createAvailabilityRule(this.resourceId, {
        dayOfWeek: this.newRuleDayOfWeek(),
        startTime: `${this.newRuleStartTime()}:00`,
        endTime: `${this.newRuleEndTime()}:00`,
      })
      .subscribe({
        next: (rule) => {
          this.creatingRule.set(false);
          this.rules.update((rules) => [...rules, rule]);
          this.notificationService.showSuccess('Availability rule added.');
        },
        error: (error: unknown) => {
          this.creatingRule.set(false);
          this.handleRuleFormError(error);
        },
      });
  }

  protected askDeleteRule(rule: AvailabilityRule): void {
    this.deleteError.set(null);
    this.deleteConfirmation.set({ kind: 'rule', rule });
  }

  // --- Blackout periods ---

  protected blackoutStatus(blackout: BlackoutPeriod): BlackoutTemporalStatus {
    const now = DateTime.utc();
    const start = DateTime.fromISO(blackout.startUtc, { zone: 'utc' });
    const end = DateTime.fromISO(blackout.endUtc, { zone: 'utc' });
    if (end <= now) {
      return 'past';
    }
    return start <= now ? 'active' : 'upcoming';
  }

  protected formatBlackoutRange(blackout: BlackoutPeriod): string {
    const zone = this.zone();
    const start = DateTime.fromISO(blackout.startUtc, { zone: 'utc' }).setZone(zone);
    const end = DateTime.fromISO(blackout.endUtc, { zone: 'utc' }).setZone(zone);
    return `${start.toFormat('d LLLL yyyy, HH:mm')} to ${end.toFormat('d LLLL yyyy, HH:mm')}`;
  }

  protected openCreateBlackoutDialog(): void {
    const today = DateTime.now().setZone(this.zone()).toFormat('yyyy-MM-dd');
    this.blackoutStartDate.set(today);
    this.blackoutStartTime.set('09:00');
    this.blackoutEndDate.set(today);
    this.blackoutEndTime.set('17:00');
    this.blackoutReason.set('');
    this.blackoutFormError.set(null);
    this.blackoutFieldErrors.set({});
    this.blackoutDialog.set({ mode: 'create' });
  }

  protected openEditBlackoutDialog(blackout: BlackoutPeriod): void {
    const zone = this.zone();
    const start = DateTime.fromISO(blackout.startUtc, { zone: 'utc' }).setZone(zone);
    const end = DateTime.fromISO(blackout.endUtc, { zone: 'utc' }).setZone(zone);
    this.blackoutStartDate.set(start.toFormat('yyyy-MM-dd'));
    this.blackoutStartTime.set(start.toFormat('HH:mm'));
    this.blackoutEndDate.set(end.toFormat('yyyy-MM-dd'));
    this.blackoutEndTime.set(end.toFormat('HH:mm'));
    this.blackoutReason.set(blackout.reason);
    this.blackoutFormError.set(null);
    this.blackoutFieldErrors.set({});
    this.blackoutDialog.set({ mode: 'edit', blackout });
  }

  protected closeBlackoutDialog(): void {
    this.blackoutDialog.set(null);
  }

  protected submitBlackoutDialog(): void {
    const dialog = this.blackoutDialog();
    if (!dialog || this.savingBlackout()) {
      return;
    }

    this.blackoutFormError.set(null);
    this.blackoutFieldErrors.set({});

    const reason = this.blackoutReason().trim();
    if (!reason) {
      this.blackoutFormError.set('Reason is required.');
      return;
    }

    const zone = this.zone();
    const start = parseStrictLocalDateTime(toLocalDateTimeString(this.blackoutStartDate(), this.blackoutStartTime()), zone);
    const end = parseStrictLocalDateTime(toLocalDateTimeString(this.blackoutEndDate(), this.blackoutEndTime()), zone);
    if (!start || !end) {
      // Most likely cause: a spring-forward daylight-saving change means this exact wall-clock time
      // never happened in the resource's timezone - see parseStrictLocalDateTime. An ambiguous
      // fall-back time is NOT rejected here - it resolves to the first (pre-fall-back) occurrence.
      this.blackoutFormError.set(
        "That date and time doesn't exist in this resource's timezone (likely a daylight-saving time change). Please choose a different time.",
      );
      return;
    }
    if (end <= start) {
      this.blackoutFormError.set('End must be after start.');
      return;
    }
    if (dialog.mode === 'create' && start < DateTime.now()) {
      this.blackoutFormError.set('A new blackout period cannot start in the past.');
      return;
    }

    const request = { startUtc: start.toUTC().toISO()!, endUtc: end.toUTC().toISO()!, reason };

    this.savingBlackout.set(true);
    const result$ =
      dialog.mode === 'create'
        ? this.resourceService.createBlackoutPeriod(this.resourceId, request)
        : this.resourceService.updateBlackoutPeriod(this.resourceId, dialog.blackout.id, request);

    result$.subscribe({
      next: (saved) => {
        this.savingBlackout.set(false);
        this.blackoutDialog.set(null);
        this.blackouts.update((blackouts) => {
          const withoutSaved = blackouts.filter((b) => b.id !== saved.id);
          return [...withoutSaved, { id: saved.id, resourceId: saved.resourceId, startUtc: saved.startUtc, endUtc: saved.endUtc, reason: saved.reason }];
        });

        if (saved.conflictingBookingIds.length > 0) {
          this.conflictWarning.set({ count: saved.conflictingBookingIds.length, ids: saved.conflictingBookingIds });
        } else {
          this.notificationService.showSuccess(dialog.mode === 'create' ? 'Blackout period added.' : 'Blackout period updated.');
        }
      },
      error: (error: unknown) => {
        this.savingBlackout.set(false);
        this.handleBlackoutFormError(error);
      },
    });
  }

  protected askDeleteBlackout(blackout: BlackoutPeriod): void {
    this.deleteError.set(null);
    this.deleteConfirmation.set({ kind: 'blackout', blackout });
  }

  // --- Shared delete confirmation ---

  protected cancelDelete(): void {
    this.deleteConfirmation.set(null);
  }

  protected confirmDelete(): void {
    const target = this.deleteConfirmation();
    if (!target || this.deleting()) {
      return;
    }

    this.deleting.set(true);
    this.deleteError.set(null);

    const result$ =
      target.kind === 'rule'
        ? this.resourceService.deleteAvailabilityRule(this.resourceId, target.rule.id)
        : this.resourceService.deleteBlackoutPeriod(this.resourceId, target.blackout.id);

    result$.subscribe({
      next: () => {
        this.deleting.set(false);
        this.deleteConfirmation.set(null);
        if (target.kind === 'rule') {
          this.rules.update((rules) => rules.filter((rule) => rule.id !== target.rule.id));
          this.notificationService.showSuccess('Availability rule deleted.');
        } else {
          this.blackouts.update((blackouts) => blackouts.filter((blackout) => blackout.id !== target.blackout.id));
          this.notificationService.showSuccess('Blackout period deleted.');
        }
      },
      error: (error: unknown) => {
        this.deleting.set(false);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.deleteError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  // --- Loading ---

  private loadResource(): void {
    this.resourceLoading.set(true);
    this.resourceError.set(null);
    this.resourceService.getResource(this.resourceId).subscribe({
      next: (resource) => {
        this.resource.set(resource);
        this.resourceLoading.set(false);
        this.loadRules();
        this.loadBlackouts();
      },
      error: (error: unknown) => {
        this.resourceError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.resourceLoading.set(false);
      },
    });
  }

  private loadRules(): void {
    this.rulesLoading.set(true);
    this.rulesError.set(null);
    this.resourceService.getAvailabilityRules(this.resourceId).subscribe({
      next: (rules) => {
        this.rules.set(rules);
        this.rulesLoading.set(false);
      },
      error: (error: unknown) => {
        this.rulesError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.rulesLoading.set(false);
      },
    });
  }

  private loadBlackouts(): void {
    this.blackoutsLoading.set(true);
    this.blackoutsError.set(null);
    this.resourceService.getBlackoutPeriods(this.resourceId).subscribe({
      next: (blackouts) => {
        this.blackouts.set(blackouts);
        this.blackoutsLoading.set(false);
      },
      error: (error: unknown) => {
        this.blackoutsError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
        this.blackoutsLoading.set(false);
      },
    });
  }

  private handleRuleFormError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.ruleFormError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError = toApiError(error);
    if (apiError.fieldErrors) {
      this.ruleFieldErrors.set(apiError.fieldErrors);
      return;
    }
    this.ruleFormError.set(apiError.detail ?? apiError.title);
  }

  private handleBlackoutFormError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.blackoutFormError.set('Something went wrong. Please try again.');
      return;
    }

    const apiError = toApiError(error);
    if (apiError.fieldErrors) {
      this.blackoutFieldErrors.set(apiError.fieldErrors);
      return;
    }
    this.blackoutFormError.set(apiError.detail ?? apiError.title);
  }
}
