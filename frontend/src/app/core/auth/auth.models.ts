export interface AuthTokens {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
}

export interface CurrentUser {
  userId: string;
  email: string;
  tenantId: string;
  roles: string[];
}
