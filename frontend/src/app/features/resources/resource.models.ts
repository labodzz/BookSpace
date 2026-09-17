// Mirrors BookSpace.Domain.Enums.ResourceStatus exactly - do not add values the API doesn't have.
export type ResourceStatus = 'Active' | 'Inactive' | 'Maintenance' | 'Archived';

export interface ResourceType {
  id: string;
  name: string;
}

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
