export interface AuthTokens {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
}

export interface CurrentUser {
  userId: string;
  email: string;
  // null for a global SysAdmin - the API omits the tenant_id claim entirely for a user with no tenant
  // (see JwtTokenGenerator) rather than emitting a sentinel value.
  tenantId: string | null;
  roles: string[];
}
