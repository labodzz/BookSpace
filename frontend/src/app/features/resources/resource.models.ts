// Mirrors BookSpace.Domain.Enums.ResourceStatus exactly - do not add values the API doesn't have.
export type ResourceStatus = 'Active' | 'Inactive' | 'Maintenance' | 'Archived';

export interface ResourceType {
  id: string;
  name: string;
}

// Create/UpdateResourceTypeRequest's body - the two commands take an identical field set, same
// reasoning as Create/UpdateBlackoutPeriodRequest below.
export interface CreateResourceTypeRequest {
  name: string;
}

export type UpdateResourceTypeRequest = CreateResourceTypeRequest;

// GetResourcesResponseItem / GetResourceResponse - the same shape is used for both the list and the
// single-resource read.
export interface ResourceSummary {
  id: string;
  resourceTypeId: string;
  name: string;
  description: string | null;
  capacity: number;
  requiresApproval: boolean;
  status: ResourceStatus;
  timeZoneId: string;
}

// CreateResourceRequest - POST /resources. Status isn't settable here; the backend always creates a
// new resource as Active.
export interface CreateResourceRequest {
  resourceTypeId: string;
  name: string;
  description: string | null;
  capacity: number;
  requiresApproval: boolean;
  timeZoneId: string;
}

// UpdateResourceCommandRequest - PUT /resources/{id}. Status excludes 'Archived' - the backend rejects
// it (use archiveResource() instead) and rejects editing an already-Archived resource at all.
export interface UpdateResourceRequest extends CreateResourceRequest {
  status: Exclude<ResourceStatus, 'Archived'>;
}

// Mirrors System.DayOfWeek's underlying int exactly (Sunday=0 ... Saturday=6) - see resource.mappers.ts
// for the wire conversion.
export type DayOfWeek = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

// GetAvailabilityRulesResponseItem / CreateAvailabilityRuleResponse. StartTime/EndTime are
// resource-local wall-clock "HH:mm:ss" strings (.NET TimeOnly's own wire format) - never UTC, and never
// converted through the viewer's own timezone; see resource-availability-manage.ts.
export interface AvailabilityRule {
  id: string;
  resourceId: string;
  dayOfWeek: DayOfWeek;
  startTime: string;
  endTime: string;
}

// CreateAvailabilityRuleCommandRequest's body (ResourceId comes from the URL). The backend supports
// only Create/Delete for this entity - there is no Update endpoint, by design (see
// docs/resource-lifecycle-and-capacity.md).
export type CreateAvailabilityRuleRequest = Omit<AvailabilityRule, 'id' | 'resourceId'>;

// GetBlackoutPeriodsResponseItem - the list endpoint's shape. Deliberately separate from BlackoutWindow
// below (the read-only availability response's embedded shape): that one has no id and is never a
// management target, this one is the actual entity a request/response should just have both of.
export interface BlackoutPeriod {
  id: string;
  resourceId: string;
  startUtc: string;
  endUtc: string;
  reason: string;
}

// Create/UpdateBlackoutPeriodCommandRequest's body (ResourceId/BlackoutId come from the URL) - the two
// commands take an identical field set (a full replacement), so Update reuses Create's shape rather
// than restating it.
export type CreateBlackoutPeriodRequest = Omit<BlackoutPeriod, 'id' | 'resourceId'>;
export type UpdateBlackoutPeriodRequest = CreateBlackoutPeriodRequest;

// Create/UpdateBlackoutPeriodResponse - adds ConflictingBookingIds, which the plain list response
// (BlackoutPeriod above) does not have. The blackout is already saved by the time this is returned;
// these are bookings that now overlap it, never automatically cancelled - see resource.service.ts.
export interface BlackoutPeriodWithConflicts extends BlackoutPeriod {
  conflictingBookingIds: string[];
}

export interface OpenPeriod {
  startUtc: string;
  endUtc: string;
}

export interface BlackoutWindow {
  startUtc: string;
  endUtc: string;
  reason: string;
}

export interface BusyPeriod {
  startUtc: string;
  endUtc: string;
  quantity: number;
}

// The capacity-aware "you can actually book this" slots - GetResourceAvailabilityResponse.BookableSlots.
// Empty whenever the resource itself isn't Active.
export interface BookableSlot {
  startUtc: string;
  endUtc: string;
  availableCapacity: number;
}

// GetResourceApproversResponseItem - GET /resources/{id}/approvers. Deliberately bare (no display data)
// - ResourceApprover is a live, DB-checked assignment, entirely separate from the global "Approver" role
// on the JWT. See resource-approvers-manage.ts for how this gets resolved into AssignedApprover below.
export interface ResourceApproverAssignment {
  id: string;
  resourceId: string;
  userId: string;
}

// AssignResourceApproverCommandRequest's body - POST /resources/{id}/approvers.
export interface AssignResourceApproverRequest {
  userId: string;
}

// How an assignment's userId resolved against the tenant's user roster (GET /users?ids=):
// - 'resolved': the user exists and currently holds the global Approver role - a normal assignment.
// - 'missing-role': the user exists but no longer holds Approver (e.g. their role was changed after
//   being assigned) - they cannot actually approve anything for this resource despite the assignment
//   still existing (see ApprovalAuthorization.EnsureCallerCanDecideAsync's role gate).
// - 'unresolved': no user with this id could be found at all (e.g. a legacy assignment surviving a
//   deleted user). Never hidden - see resource-approvers-manage.html's warning banner for either state.
export type ApproverResolution = 'resolved' | 'missing-role' | 'unresolved';

// Display-ready row for the "current approvers" list - composes ResourceApproverAssignment with
// whatever GetUsersQueryRequest could resolve about that userId. firstName/lastName/email/roles are
// null/empty only when resolution is 'unresolved'.
export interface AssignedApprover {
  assignmentId: string;
  userId: string;
  resolution: ApproverResolution;
  firstName: string | null;
  lastName: string | null;
  email: string | null;
  roles: string[];
}

// GetResourceAvailabilityResponse - GET /resources/{id}/availability?from=&to=, capped server-side to a
// 92-day range.
export interface ResourceAvailability {
  resourceId: string;
  fromDate: string;
  toDate: string;
  timeZoneId: string;
  capacity: number;
  openPeriods: OpenPeriod[];
  blackouts: BlackoutWindow[];
  busyPeriods: BusyPeriod[];
  bookableSlots: BookableSlot[];
}
