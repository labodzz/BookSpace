import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { toApiError } from '../../../core/http/api-error';
import { NotificationService } from '../../../core/notifications/notification.service';
import { CalendarSubscriptionService } from '../calendar-subscription.service';
import { toLocalDateTime } from '../local-time.util';

// Lets a member turn their own Confirmed bookings into a private, subscribable ICS feed an external
// calendar app can poll - see docs/calendar-subscription-feed.md. The raw feed URL is only ever held here
// as a transient signal (never persisted to localStorage, never logged): once this component is
// destroyed or the page reloads, it is gone for good, exactly matching what the backend itself can show
// (TokenHash, never the raw token, is all that is ever stored server-side).
@Component({
  selector: 'app-calendar-subscription-panel',
  imports: [],
  templateUrl: './calendar-subscription-panel.html',
  styleUrl: './calendar-subscription-panel.scss',
})
export class CalendarSubscriptionPanelComponent {
  private readonly calendarSubscriptionService = inject(CalendarSubscriptionService);
  private readonly notificationService = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  private copiedResetTimeoutId: ReturnType<typeof setTimeout> | undefined;

  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);
  protected readonly hasActiveSubscription = signal(false);
  protected readonly createdAtUtc = signal<string | null>(null);

  // Only ever populated by THIS session's own Create/Regenerate response - never fetched from the status
  // endpoint, which deliberately never returns it (see CalendarSubscriptionStatus).
  protected readonly feedUrl = signal<string | null>(null);
  protected readonly actionPending = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly copied = signal(false);
  protected readonly confirmingRevoke = signal(false);

  constructor() {
    this.loadStatus();
    this.destroyRef.onDestroy(() => clearTimeout(this.copiedResetTimeoutId));
  }

  protected retry(): void {
    this.loadStatus();
  }

  protected formattedCreatedAt(): string {
    const createdAtUtc = this.createdAtUtc();
    return createdAtUtc ? toLocalDateTime(createdAtUtc).toFormat('LLL d, yyyy') : '';
  }

  protected create(): void {
    this.runAction(this.calendarSubscriptionService.create(), (token) => {
      this.notificationService.showSuccess('Calendar subscription created.');
      this.applyToken(token.feedUrl, token.createdAtUtc);
    });
  }

  protected regenerate(): void {
    this.runAction(this.calendarSubscriptionService.regenerate(), (token) => {
      this.notificationService.showSuccess('Calendar subscription regenerated - the previous link no longer works.');
      this.applyToken(token.feedUrl, token.createdAtUtc);
    });
  }

  protected askToRevoke(): void {
    this.confirmingRevoke.set(true);
  }

  protected cancelRevoke(): void {
    this.confirmingRevoke.set(false);
  }

  protected confirmRevoke(): void {
    this.confirmingRevoke.set(false);
    this.runAction(this.calendarSubscriptionService.revoke(), () => {
      this.notificationService.showSuccess('Calendar subscription revoked.');
      this.hasActiveSubscription.set(false);
      this.createdAtUtc.set(null);
      this.feedUrl.set(null);
    });
  }

  protected async copyFeedUrl(): Promise<void> {
    const feedUrl = this.feedUrl();
    if (!feedUrl) {
      return;
    }
    if (typeof navigator === 'undefined' || !navigator.clipboard) {
      this.notificationService.showError("Couldn't access the clipboard - copy the link manually.");
      return;
    }
    try {
      await navigator.clipboard.writeText(feedUrl);
      this.copied.set(true);
      // Cleared on destroy (a navigation away within the 3s window must not fire this later, against a
      // signal nothing is reading anymore) and any previous pending reset is cleared too, so rapid
      // repeated copies don't race each other's timeout.
      clearTimeout(this.copiedResetTimeoutId);
      this.copiedResetTimeoutId = setTimeout(() => this.copied.set(false), 3000);
    } catch {
      this.notificationService.showError("Couldn't access the clipboard - copy the link manually.");
    }
  }

  private applyToken(feedUrl: string, createdAtUtc: string): void {
    this.hasActiveSubscription.set(true);
    this.createdAtUtc.set(createdAtUtc);
    this.feedUrl.set(feedUrl);
  }

  private runAction<T>(request: Observable<T>, onSuccess: (value: T) => void): void {
    this.actionPending.set(true);
    this.actionError.set(null);
    request.subscribe({
      next: (value) => {
        this.actionPending.set(false);
        onSuccess(value);
      },
      error: (error: unknown) => {
        this.actionPending.set(false);
        const apiError = error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' };
        this.actionError.set(apiError.detail ?? apiError.title);
      },
    });
  }

  private loadStatus(): void {
    this.loading.set(true);
    this.loadError.set(false);
    this.calendarSubscriptionService.getStatus().subscribe({
      next: (status) => {
        this.loading.set(false);
        this.hasActiveSubscription.set(status.hasActiveSubscription);
        this.createdAtUtc.set(status.createdAtUtc);
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set(true);
      },
    });
  }
}
