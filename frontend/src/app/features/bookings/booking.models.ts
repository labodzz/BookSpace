// Mirrors BookSpace.Domain.Enums.BookingStatus exactly - do not add values the API doesn't have.
export type BookingStatus = 'Pending' | 'Confirmed' | 'Rejected' | 'Cancelled' | 'Completed' | 'NoShow';

// GetOwnBookingsResponseItem. CancelledByAdmin is server-derived: true only when a TenantAdmin/SysAdmin
// cancelled someone else's booking, so the UI can tell "you cancelled this" apart from "this was
// administratively cancelled" without needing to know the current user's own id.
export interface OwnBooking {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  status: BookingStatus;
  cancelledAtUtc: string | null;
  cancelledByAdmin: boolean;
  cancellationReason: string | null;
  seriesId: string | null;
  // The resource's own IANA zone (as of now, not a snapshot from booking time) - lets the UI show this
  // booking's local time at the resource alongside the viewer's own local time.
  timeZoneId: string;
}

// getOwnBookingsInRange's result: `truncated` is true when the safety page cap was hit while the
// server still reported more results remaining, so a caller can tell "this range's bookings" apart
// from "the first N of this range's bookings" instead of silently treating the latter as complete.
export interface RangeBookingsResult {
  items: OwnBooking[];
  truncated: boolean;
}

export interface CreateBookingRequest {
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
}

export interface CreateBookingResponse {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  status: BookingStatus;
  timeZoneId: string;
}

export interface CancelBookingResponse {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  status: BookingStatus;
  cascadedOccurrenceIds: string[];
}

// Mirrors BookSpace.Domain.Enums.RecurrenceFrequency, minus Yearly - the API's own validator rejects
// Yearly, so it's deliberately left out here rather than offered and always failing.
export type RecurrenceFrequency = 'Daily' | 'Weekly' | 'Monthly';

// CreateRecurringSeriesRequest. There is no day-of-week picker in the API - recurrence is purely
// "every Interval days/weeks/months starting at StartDate," so that's the only shape this form offers.
export interface CreateRecurringSeriesRequest {
  resourceId: string;
  startDate: string;
  startTime: string;
  endTime: string;
  frequency: RecurrenceFrequency;
  interval: number;
  endDate: string | null;
  occurrenceCount: number | null;
  quantity: number;
}

export interface RecurringSeriesConflict {
  date: string;
  reason: string;
}

export interface RecurringSeriesOccurrence {
  id: string;
  startUtc: string;
  endUtc: string;
  status: BookingStatus;
}

export interface CreateRecurringSeriesResponse {
  seriesId: string;
  resourceId: string;
  requestedOccurrenceCount: number;
  createdOccurrences: RecurringSeriesOccurrence[];
  conflicts: RecurringSeriesConflict[];
  // One per series (a series has exactly one resource) - see OwnBooking.timeZoneId for what it's for.
  timeZoneId: string;
}
