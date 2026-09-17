// Mirrors BookSpace.Application.Common.PagedResult<T> - the shape returned by every paginated list
// endpoint (GET /resources, GET /bookings, GET /users).
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}
