export const environment = {
  production: true,
  // The backend's own controllers are mounted under '/api' (see BookSpace.Api/Controllers) so they can
  // never collide with an Angular client route of the same name (e.g. '/bookings', '/resources') when
  // this build is served from the same origin as the API - see docs/architecture.md.
  apiUrl: '/api',
};
