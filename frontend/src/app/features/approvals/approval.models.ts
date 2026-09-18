// GetPendingApprovalsResponseItem. Not paginated - a plain array, mirroring the API. seriesId lets the
// UI group an approver's queue by recurring series (e.g. "approve all 100 pending in this series" instead
// of one at a time) - null for a plain one-off booking.
export interface PendingApproval {
  bookingId: string;
  resourceId: string;
  userId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  expiresAtUtc: string;
  seriesId: string | null;
}

// DecideBookingApprovalRequest - used by reject. DecisionNote is optional; the API does not require a
// reason on reject.
export interface DecideBookingApprovalRequest {
  decisionNote?: string | null;
}

// ApproveBookingRequest - approveRemainingSeries approves this booking AND every other still-Pending
// occurrence in the same series in one call. Ignored (no error) for a booking with no series.
export interface ApproveBookingRequest {
  decisionNote?: string | null;
  approveRemainingSeries?: boolean;
}

// RejectBookingResponse. status is the API's raw numeric enum code (BookingStatus) - unused by the
// approval queue today (it only needs success/failure), so it is left untranslated rather than pulling
// in the booking-status mapper for a value nothing reads.
export interface BookingApprovalDecisionResponse {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  status: number;
}

// A sibling occurrence approveRemainingSeries could NOT approve (still Pending) - never force-approved,
// never silently dropped. reason is a stable ErrorCode-like string (Booking.BlackoutConflict, etc.).
export interface ApprovalSeriesConflict {
  bookingId: string;
  reason: string;
}

// ApproveBookingResponse - CascadedApprovedOccurrenceIds/CascadedConflicts are only non-empty when
// approveRemainingSeries was true and the booking belonged to a series.
export interface ApproveBookingResponse {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  quantity: number;
  status: number;
  cascadedApprovedOccurrenceIds: string[];
  cascadedConflicts: ApprovalSeriesConflict[];
}
