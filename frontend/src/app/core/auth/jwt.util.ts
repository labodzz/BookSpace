import { CurrentUser } from './auth.models';

// The API signs role claims with System.Security.Claims.ClaimTypes.Role, whose string value is this
// long claims-namespace URI - not the short "role" name - because JwtTokenGenerator writes claims
// with `new Claim(ClaimTypes.Role, role)` and MapInboundClaims=false only changes how the server
// reads incoming tokens, not what's written into the ones it issues. Confirmed against a real token
// from POST /auth/login - ClaimTypes.Role is the 2008 microsoft.com URI, not the 2005 xmlsoap.org one
// other claim types (like Name) use.
const ROLE_CLAIM_TYPE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

// Decodes the access token's payload to read the identity BookSpace's own API already vouched for -
// this never verifies the signature (that's the API's job on every request), so it must only be used
// to drive UI display and route guards, never as a substitute for server-side authorization.
export function decodeAccessToken(accessToken: string): CurrentUser | null {
  const payload = decodeJwtPayload(accessToken);
  if (!payload) {
    return null;
  }

  const userId = payload['sub'];
  const email = payload['email'];
  const tenantId = payload['tenant_id'];
  if (typeof userId !== 'string' || typeof email !== 'string' || typeof tenantId !== 'string') {
    return null;
  }

  const roleClaim = payload[ROLE_CLAIM_TYPE];
  const roles = Array.isArray(roleClaim)
    ? roleClaim.filter((role): role is string => typeof role === 'string')
    : typeof roleClaim === 'string'
      ? [roleClaim]
      : [];

  return { userId, email, tenantId, roles };
}

function decodeJwtPayload(token: string): Record<string, unknown> | null {
  const parts = token.split('.');
  if (parts.length !== 3) {
    return null;
  }

  try {
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
    const decoded = atob(padded);
    const json = decodeURIComponent(
      Array.from(decoded)
        .map((char) => '%' + char.charCodeAt(0).toString(16).padStart(2, '0'))
        .join(''),
    );
    const parsed: unknown = JSON.parse(json);
    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}
