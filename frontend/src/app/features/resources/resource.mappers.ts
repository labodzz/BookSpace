import { ResourceStatus, ResourceSummary } from './resource.models';

// Confirmed live against the running API: BookSpace serializes every enum in a JSON body as its raw
// underlying number (System.Text.Json's default - no JsonStringEnumConverter is registered anywhere in
// the pipeline). This array is the wire contract: index = the enum's underlying int, matching
// BookSpace.Domain.Enums.ResourceStatus exactly (Active, Inactive, Maintenance, Archived). Query-string
// filters are unaffected - ASP.NET Core's route/query model binder accepts the string name there via a
// separate mechanism from System.Text.Json, confirmed the same way.
const RESOURCE_STATUS_BY_CODE: readonly ResourceStatus[] = ['Active', 'Inactive', 'Maintenance', 'Archived'];

export function toResourceStatus(code: number): ResourceStatus {
  return RESOURCE_STATUS_BY_CODE[code] ?? 'Active';
}

export interface ResourceSummaryWire extends Omit<ResourceSummary, 'status'> {
  status: number;
}

export function mapResourceSummary(wire: ResourceSummaryWire): ResourceSummary {
  return { ...wire, status: toResourceStatus(wire.status) };
}
