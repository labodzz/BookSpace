import { decodeAccessToken } from './jwt.util';

const ROLE_CLAIM_TYPE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64Url = (value: string) =>
    btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  const header = base64Url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64Url(JSON.stringify(payload));
  return `${header}.${body}.signature-not-verified-client-side`;
}

describe('decodeAccessToken', () => {
  it('reads sub/email/tenant_id and a single role encoded as a plain string', () => {
    const token = tokenWithPayload({
      sub: 'b1f2e3d4-0000-0000-0000-000000000001',
      email: 'ana@bookspace.test',
      tenant_id: 'b1f2e3d4-0000-0000-0000-00000000000a',
      [ROLE_CLAIM_TYPE]: 'Member',
    });

    expect(decodeAccessToken(token)).toEqual({
      userId: 'b1f2e3d4-0000-0000-0000-000000000001',
      email: 'ana@bookspace.test',
      tenantId: 'b1f2e3d4-0000-0000-0000-00000000000a',
      roles: ['Member'],
    });
  });

  it('reads multiple roles encoded as a JSON array', () => {
    const token = tokenWithPayload({
      sub: 'b1f2e3d4-0000-0000-0000-000000000001',
      email: 'ana@bookspace.test',
      tenant_id: 'b1f2e3d4-0000-0000-0000-00000000000a',
      [ROLE_CLAIM_TYPE]: ['TenantAdmin', 'Approver'],
    });

    expect(decodeAccessToken(token)?.roles).toEqual(['TenantAdmin', 'Approver']);
  });

  it('defaults to no roles when the claim is absent', () => {
    const token = tokenWithPayload({
      sub: 'b1f2e3d4-0000-0000-0000-000000000001',
      email: 'ana@bookspace.test',
      tenant_id: 'b1f2e3d4-0000-0000-0000-00000000000a',
    });

    expect(decodeAccessToken(token)?.roles).toEqual([]);
  });

  it('returns null for a malformed token', () => {
    expect(decodeAccessToken('not-a-jwt')).toBeNull();
  });

  it('returns null when a required claim is missing', () => {
    const token = tokenWithPayload({ sub: 'b1f2e3d4-0000-0000-0000-000000000001' });

    expect(decodeAccessToken(token)).toBeNull();
  });

  // Pinned to a real access token captured from a live POST /auth/login response (dev-seeded
  // member@acme.test) - a regression test for the role claim's exact URI, which a hand-typed fixture
  // would happily "confirm" even after silently drifting from what the API actually issues.
  it('decodes a real access token issued by the running API', () => {
    const realAccessToken =
      'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJhMjZlMjQyYy1lN2NiLTRlNmYtOGMzNi1lNmFlNjc4Njk4MDYiLCJlbWFpbCI6Im1lbWJlckBhY21lLnRlc3QiLCJ0ZW5hbnRfaWQiOiIyNGY2ZTZlMy1kMzczLTQxZmItYmI0Yi04MzcyOTQ1MjRlMDQiLCJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL3JvbGUiOiJNZW1iZXIiLCJleHAiOjE3ODkzNzc0NDMsImlzcyI6IkJvb2tTcGFjZSIsImF1ZCI6IkJvb2tTcGFjZSJ9.D5u4oXyy-Yt2NeQsuWW_RoLjo9Dtz-ExLexAFqYzRyI';

    expect(decodeAccessToken(realAccessToken)).toEqual({
      userId: 'a26e242c-e7cb-4e6f-8c36-e6ae67869806',
      email: 'member@acme.test',
      tenantId: '24f6e6e3-d373-41fb-bb4b-837294524e04',
      roles: ['Member'],
    });
  });
});
