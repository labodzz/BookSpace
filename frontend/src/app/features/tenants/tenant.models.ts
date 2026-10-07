export interface CreateTenantRequest {
  name: string;
  defaultTimeZoneId: string;
  adminFirstName: string;
  adminLastName: string;
  adminEmail: string;
}

// Never a token or an invitation URL - see CreateTenantResponse's own doc comment on the backend.
// adminUserId/tenantId are returned specifically so the "Resend invitation" action can target this exact
// tenant/user without a tenant list/detail page existing to look them up from.
export interface CreateTenantResult {
  tenantId: string;
  tenantName: string;
  adminUserId: string;
  adminEmail: string;
}

export interface ResendTenantAdminInvitationResult {
  tenantId: string;
  adminUserId: string;
  adminEmail: string;
  invitationExpiresAtUtc: string;
}

// Mirrors BookSpace.Domain.Enums.TenantStatus exactly - do not add values the API doesn't have. Deleted
// is not reversible through this API (see TenantStatus's own backend doc comment) - there is no
// "undelete" action anywhere in this UI.
export type TenantStatus = 'Active' | 'Suspended' | 'Deleted';

// Platform data and aggregates only - never a booking/resource count, never any ordinary member's name.
// See GetTenantsQueryRequest/GetTenantQueryRequest on the backend.
export interface TenantSummary {
  id: string;
  name: string;
  status: TenantStatus;
  createdAtUtc: string;
  totalUserCount: number;
  activeUserCount: number;
  invitedUserCount: number;
  inactiveUserCount: number;
  activeTenantAdminEmails: string[];
  hasActiveTenantAdmin: boolean;
}

// A still-Invited TenantAdmin - exactly enough to resend or revoke their invitation. Never a name or
// the invitation token/expiry itself - see PendingTenantAdminContact on the backend.
export interface PendingTenantAdminContact {
  userId: string;
  email: string;
}

export interface TenantDetail extends TenantSummary {
  defaultTimeZoneId: string;
  pendingTenantAdminInvitations: PendingTenantAdminContact[];
}

// TenantStatusResponse - the shared result shape for rename/suspend/reactivate/delete.
export interface TenantStatusResult {
  id: string;
  name: string;
  status: TenantStatus;
}

export interface UpdateTenantRequest {
  name: string;
}

export interface DeleteTenantRequest {
  confirmName: string;
}

export interface InviteReplacementTenantAdminRequest {
  firstName: string;
  lastName: string;
  email: string;
}

export interface InviteReplacementTenantAdminResult {
  tenantId: string;
  adminUserId: string;
  adminEmail: string;
}
