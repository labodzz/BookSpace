import { UserDetail, UserStatus, UserSummary } from './user.models';

// Confirmed live against BookSpace.Api.Tests/Users/UserAdministrationEndpointsTests.cs's own assertions
// (Assert.Equal(0, body!.Status) for Active, 2 for Inactive) - BookSpace serializes every enum in a JSON
// body as its raw underlying number (System.Text.Json's default - no JsonStringEnumConverter is
// registered), the same convention resource.mappers.ts already documents for ResourceStatus. Index =
// UserStatus's underlying int, matching BookSpace.Domain.Enums.UserStatus exactly (Active, Invited,
// Inactive). The GET /users status QUERY-STRING filter is unaffected - ASP.NET Core's model binder
// accepts the string name there, same as every other enum query filter in this app.
const USER_STATUS_BY_CODE: readonly UserStatus[] = ['Active', 'Invited', 'Inactive'];

// An unrecognized code (a future status this build doesn't know about yet) falls back to 'Active' rather
// than crashing the page - the same fail-safe convention toResourceStatus already established, kept
// identical here for consistency rather than inventing a different fallback rule for this one enum.
export function toUserStatus(code: number): UserStatus {
  return USER_STATUS_BY_CODE[code] ?? 'Active';
}

export interface UserSummaryWire extends Omit<UserSummary, 'status'> {
  status: number;
}

export function mapUserSummary(wire: UserSummaryWire): UserSummary {
  return { ...wire, status: toUserStatus(wire.status) };
}

export interface UserDetailWire extends Omit<UserDetail, 'status'> {
  status: number;
}

export function mapUserDetail(wire: UserDetailWire): UserDetail {
  return { ...wire, status: toUserStatus(wire.status) };
}
