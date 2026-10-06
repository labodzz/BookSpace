// Wire shapes exactly match CalendarSubscriptionStatusResponse/CalendarSubscriptionTokenResponse on the
// backend (see BookSpace.Application/Calendar) - no mapping needed, both sides already use camelCase.
export interface CalendarSubscriptionStatus {
  hasActiveSubscription: boolean;
  createdAtUtc: string | null;
}

// FeedUrl is the raw token's one and only appearance anywhere the frontend ever sees it - it is never
// persisted (not in a service field that outlives the component, not in localStorage) and the backend
// itself can never return it again after this one response (only its hash is stored server-side).
export interface CalendarSubscriptionToken {
  feedUrl: string;
  createdAtUtc: string;
}
