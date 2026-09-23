import { UserSummary } from '../users/user.models';
import { AssignedApprover, AvailabilityRule, DayOfWeek, ResourceApproverAssignment, ResourceStatus, ResourceSummary } from './resource.models';

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

// The reverse direction, needed when SENDING a request body: System.Text.Json expects the same raw
// number on the way in as it produces on the way out, since no JsonStringEnumConverter is registered.
export function fromResourceStatus(status: ResourceStatus): number {
  return RESOURCE_STATUS_BY_CODE.indexOf(status);
}

export interface ResourceSummaryWire extends Omit<ResourceSummary, 'status'> {
  status: number;
}

export function mapResourceSummary(wire: ResourceSummaryWire): ResourceSummary {
  return { ...wire, status: toResourceStatus(wire.status) };
}

// Same raw-number wire convention as ResourceStatus above, this time for System.DayOfWeek
// (Sunday=0 ... Saturday=6) - confirmed against BookSpace.Api.Tests' own
// `new { dayOfWeek = DayOfWeek.Monday, ... }` request bodies, which round-trip through the same
// unconfigured System.Text.Json serializer as everything else in this API.
const DAY_OF_WEEK_BY_CODE: readonly DayOfWeek[] = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function toDayOfWeek(code: number): DayOfWeek {
  return DAY_OF_WEEK_BY_CODE[code] ?? 'Sunday';
}

export function fromDayOfWeek(day: DayOfWeek): number {
  return DAY_OF_WEEK_BY_CODE.indexOf(day);
}

export interface AvailabilityRuleWire extends Omit<AvailabilityRule, 'dayOfWeek'> {
  dayOfWeek: number;
}

export function mapAvailabilityRule(wire: AvailabilityRuleWire): AvailabilityRule {
  return { ...wire, dayOfWeek: toDayOfWeek(wire.dayOfWeek) };
}

// Mirrors AssignResourceApproverCommandHandler's own eligibility check exactly (Approver, TenantAdmin,
// or SysAdmin all qualify) - NOT just "Approver". This is deliberately broader than the eligible-user
// PICKER's own role filter (which only ever searches for "Approver" specifically, since a TenantAdmin/
// SysAdmin already bypasses the per-resource check entirely and gains nothing from being assigned - see
// resource-approvers-manage.ts). Using the narrower "Approver only" rule here would incorrectly flag a
// legitimate TenantAdmin/SysAdmin assignment as "missing role".
const APPROVAL_CAPABLE_ROLES = ['Approver', 'TenantAdmin', 'SysAdmin'];

export function mapAssignedApprover(assignment: ResourceApproverAssignment, user: UserSummary | undefined): AssignedApprover {
  if (!user) {
    return {
      assignmentId: assignment.id,
      userId: assignment.userId,
      resolution: 'unresolved',
      firstName: null,
      lastName: null,
      email: null,
      roles: [],
    };
  }

  return {
    assignmentId: assignment.id,
    userId: assignment.userId,
    resolution: user.roles.some((role) => APPROVAL_CAPABLE_ROLES.includes(role)) ? 'resolved' : 'missing-role',
    firstName: user.firstName,
    lastName: user.lastName,
    email: user.email,
    roles: user.roles,
  };
}
