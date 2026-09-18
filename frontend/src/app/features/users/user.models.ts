// UserSummaryResponse - GET /users (TenantAdmin/SysAdmin only).
export interface UserSummary {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  tenantId: string;
}
