import { TenantDetail, TenantStatus, TenantStatusResult, TenantSummary } from './tenant.models';

// BookSpace serializes every enum in a JSON body as its raw underlying number (System.Text.Json's
// default - no JsonStringEnumConverter is registered), the same convention user.mappers.ts/
// resource.mappers.ts already document for their own enums. Index = TenantStatus's underlying int,
// matching BookSpace.Domain.Enums.TenantStatus exactly (Active, Suspended, Deleted). The GET /tenants
// status QUERY-STRING filter is unaffected - ASP.NET Core's model binder accepts the string name there,
// same as every other enum query filter in this app.
const TENANT_STATUS_BY_CODE: readonly TenantStatus[] = ['Active', 'Suspended', 'Deleted'];

// An unrecognized code (a future status this build doesn't know about yet) falls back to 'Active' rather
// than crashing the page - the same fail-safe convention toUserStatus/toResourceStatus already establish.
export function toTenantStatus(code: number): TenantStatus {
  return TENANT_STATUS_BY_CODE[code] ?? 'Active';
}

export interface TenantSummaryWire extends Omit<TenantSummary, 'status'> {
  status: number;
}

export function mapTenantSummary(wire: TenantSummaryWire): TenantSummary {
  return { ...wire, status: toTenantStatus(wire.status) };
}

export interface TenantDetailWire extends Omit<TenantDetail, 'status'> {
  status: number;
}

export function mapTenantDetail(wire: TenantDetailWire): TenantDetail {
  return { ...wire, status: toTenantStatus(wire.status) };
}

export interface TenantStatusResultWire extends Omit<TenantStatusResult, 'status'> {
  status: number;
}

export function mapTenantStatusResult(wire: TenantStatusResultWire): TenantStatusResult {
  return { ...wire, status: toTenantStatus(wire.status) };
}
