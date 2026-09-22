// Mirrors BookSpace.Domain.Enums.UserStatus exactly - do not add values the API doesn't have. Invited
// users exist as a real row (email reserved, roles may already be staged) but cannot log in yet; no
// invitation/onboarding UI is exposed for this status in this batch - see user.service.ts.
export type UserStatus = 'Active' | 'Invited' | 'Inactive';

const USER_STATUSES: readonly UserStatus[] = ['Active', 'Invited', 'Inactive'];

// Validates a value of unknown origin (e.g. a query param read back from the URL, which a viewer can
// freely edit by hand) before it's trusted as a UserStatus - never crashes on garbage input, just
// reports it isn't one.
export function isUserStatus(value: string | null): value is UserStatus {
  return !!value && (USER_STATUSES as readonly string[]).includes(value);
}

// The global roles this frontend batch can ever assign or remove through POST/DELETE
// /users/{id}/roles/... - mirrors UserAdministrationGuard.DelegableRoles exactly. SysAdmin is
// deliberately excluded: the backend's own validators reject it for every caller (see
// docs/user-administration.md §2), and no documentation anywhere defines a delegation rule for it, so
// this UI never offers a control for it either.
export const DELEGABLE_ROLES: readonly string[] = ['Member', 'Approver', 'TenantAdmin'];

// Every global role that can exist on a user, delegable or not - used only for the read-only role
// FILTER on the list page (an existing SysAdmin account, seeded outside this UI, must still be
// filterable), never for an assignment control.
export const ALL_GLOBAL_ROLES: readonly string[] = ['Member', 'Approver', 'TenantAdmin', 'SysAdmin'];

// UserSummaryResponse - GET /users (TenantAdmin/SysAdmin only). roles is every global role name the
// user holds (e.g. "Approver", "TenantAdmin") - not a per-resource assignment, which is a completely
// separate, live-checked concept (see resource.models.ts's AssignedApprover).
export interface UserSummary {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  tenantId: string;
  status: UserStatus;
  roles: string[];
}

// GetUserResponse - GET /users/{id}. Adds CreatedAtUtc and PendingInvitationExpiresAtUtc, which the list
// response never carries. pendingInvitationExpiresAtUtc is set only when status is 'Invited' and an
// active (not accepted, not revoked) invitation currently exists - never the invitation token itself,
// which no endpoint this batch calls ever returns (see docs/user-administration.md §4/§7).
export interface UserDetail extends UserSummary {
  createdAtUtc: string;
  pendingInvitationExpiresAtUtc: string | null;
}

// UpdateUserCommandRequest's body - PUT /users/{id}. Deliberately narrow, matching the backend exactly:
// Email is immutable through this endpoint, and Status/roles each have their own dedicated action below.
export interface UpdateUserRequest {
  firstName: string;
  lastName: string;
}

// AssignUserRoleResponse - POST /users/{id}/roles.
export interface AssignRoleResponse {
  userId: string;
  roles: string[];
}
