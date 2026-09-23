import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ResourceService } from './resource.service';

const RESOURCES_URL = `${environment.apiUrl}/resources`;
const RESOURCE_URL = `${RESOURCES_URL}/resource-1`;
const TYPES_URL = `${environment.apiUrl}/resource-types`;
const USERS_URL = `${environment.apiUrl}/users`;

function wireResource(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'resource-1',
    resourceTypeId: 'type-1',
    name: 'Falcon Room',
    description: null,
    capacity: 4,
    requiresApproval: false,
    status: 0, // ResourceStatus.Active
    timeZoneId: 'UTC',
    ...overrides,
  };
}

describe('ResourceService', () => {
  let service: ResourceService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ResourceService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('createResource POSTs the request as-is and maps the numeric status back to a string', () => {
    let result: unknown;
    service
      .createResource({
        resourceTypeId: 'type-1',
        name: 'Falcon Room',
        description: null,
        capacity: 4,
        requiresApproval: false,
        timeZoneId: 'UTC',
      })
      .subscribe((resource) => (result = resource));

    const req = httpTesting.expectOne((r) => r.url === RESOURCES_URL && r.method === 'POST');
    expect(req.request.body).toEqual({
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      timeZoneId: 'UTC',
    });
    req.flush(wireResource());

    expect(result).toEqual({
      id: 'resource-1',
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: null,
      capacity: 4,
      requiresApproval: false,
      status: 'Active',
      timeZoneId: 'UTC',
    });
  });

  it('createResource caches the created resource for getResourceCached', () => {
    service
      .createResource({
        resourceTypeId: 'type-1',
        name: 'Falcon Room',
        description: null,
        capacity: 4,
        requiresApproval: false,
        timeZoneId: 'UTC',
      })
      .subscribe();
    httpTesting.expectOne((r) => r.url === RESOURCES_URL && r.method === 'POST').flush(wireResource());

    let cached: unknown;
    service.getResourceCached('resource-1').subscribe((resource) => (cached = resource));

    expect(cached).toEqual(expect.objectContaining({ id: 'resource-1', name: 'Falcon Room' }));
    httpTesting.verify(); // no further HTTP request needed - it was served from the cache
  });

  it('updateResource PUTs to the resource id and sends the status as its numeric wire code, not the string', () => {
    let result: unknown;
    service
      .updateResource('resource-1', {
        resourceTypeId: 'type-1',
        name: 'Falcon Room (renamed)',
        description: null,
        capacity: 6,
        requiresApproval: true,
        timeZoneId: 'Europe/Sarajevo',
        status: 'Inactive',
      })
      .subscribe((resource) => (result = resource));

    const req = httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT');
    expect(req.request.body.status).toBe(1); // ResourceStatus.Inactive
    expect(req.request.body.name).toBe('Falcon Room (renamed)');
    req.flush(wireResource({ name: 'Falcon Room (renamed)', capacity: 6, requiresApproval: true, timeZoneId: 'Europe/Sarajevo', status: 1 }));

    expect(result).toEqual(
      expect.objectContaining({ name: 'Falcon Room (renamed)', capacity: 6, requiresApproval: true, status: 'Inactive' }),
    );
  });

  it('archiveResource DELETEs the resource id and maps the resulting Archived status', () => {
    let result: unknown;
    service.archiveResource('resource-1').subscribe((resource) => (result = resource));

    const req = httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'DELETE');
    req.flush(wireResource({ status: 3 })); // ResourceStatus.Archived

    expect(result).toEqual(expect.objectContaining({ status: 'Archived' }));
  });

  it('updateResource refreshes the cached entry getResourceCached serves for the same id', () => {
    let firstLoad: unknown;
    service.getResourceCached('resource-1').subscribe((resource) => (firstLoad = resource));
    httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(wireResource());
    expect(firstLoad).toEqual(expect.objectContaining({ name: 'Falcon Room' }));

    service
      .updateResource('resource-1', {
        resourceTypeId: 'type-1',
        name: 'Falcon Room (renamed)',
        description: null,
        capacity: 4,
        requiresApproval: false,
        timeZoneId: 'UTC',
        status: 'Active',
      })
      .subscribe();
    httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT').flush(wireResource({ name: 'Falcon Room (renamed)' }));

    let secondLoad: unknown;
    service.getResourceCached('resource-1').subscribe((resource) => (secondLoad = resource));
    expect(secondLoad).toEqual(expect.objectContaining({ name: 'Falcon Room (renamed)' }));
    httpTesting.verify(); // the second getResourceCached call must not re-fetch - it should see the update
  });

  describe('availability rules', () => {
    it('getAvailabilityRules maps each raw numeric dayOfWeek onto its string form', () => {
      let result: unknown;
      service.getAvailabilityRules('resource-1').subscribe((rules) => (result = rules));

      httpTesting.expectOne(`${RESOURCE_URL}/availability-rules`).flush([
        { id: 'rule-1', resourceId: 'resource-1', dayOfWeek: 0, startTime: '09:00:00', endTime: '17:00:00' },
        { id: 'rule-2', resourceId: 'resource-1', dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' },
      ]);

      expect(result).toEqual([
        { id: 'rule-1', resourceId: 'resource-1', dayOfWeek: 'Sunday', startTime: '09:00:00', endTime: '17:00:00' },
        { id: 'rule-2', resourceId: 'resource-1', dayOfWeek: 'Monday', startTime: '09:00:00', endTime: '17:00:00' },
      ]);
    });

    it('createAvailabilityRule POSTs dayOfWeek as its numeric wire code, not the string', () => {
      service.createAvailabilityRule('resource-1', { dayOfWeek: 'Monday', startTime: '09:00:00', endTime: '17:00:00' }).subscribe();

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/availability-rules` && r.method === 'POST');
      expect(req.request.body).toEqual({ dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' });
      req.flush({ id: 'rule-1', resourceId: 'resource-1', dayOfWeek: 1, startTime: '09:00:00', endTime: '17:00:00' });
    });

    it('deleteAvailabilityRule DELETEs the specific rule id under the resource', () => {
      service.deleteAvailabilityRule('resource-1', 'rule-1').subscribe();

      httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/availability-rules/rule-1` && r.method === 'DELETE').flush(null);
    });
  });

  describe('blackout periods', () => {
    it('createBlackoutPeriod POSTs the request as-is and surfaces conflictingBookingIds unchanged', () => {
      let result: unknown;
      service
        .createBlackoutPeriod('resource-1', { startUtc: '2026-01-01T00:00:00Z', endUtc: '2026-01-02T00:00:00Z', reason: 'Maintenance' })
        .subscribe((period) => (result = period));

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/blackout-periods` && r.method === 'POST');
      expect(req.request.body).toEqual({ startUtc: '2026-01-01T00:00:00Z', endUtc: '2026-01-02T00:00:00Z', reason: 'Maintenance' });
      req.flush({
        id: 'blackout-1', resourceId: 'resource-1', startUtc: '2026-01-01T00:00:00Z', endUtc: '2026-01-02T00:00:00Z',
        reason: 'Maintenance', conflictingBookingIds: ['booking-1'],
      });

      expect(result).toEqual(expect.objectContaining({ conflictingBookingIds: ['booking-1'] }));
    });

    it('updateBlackoutPeriod PUTs to the specific blackout id under the resource', () => {
      service
        .updateBlackoutPeriod('resource-1', 'blackout-1', { startUtc: '2026-01-01T00:00:00Z', endUtc: '2026-01-02T00:00:00Z', reason: 'Changed' })
        .subscribe();

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/blackout-periods/blackout-1` && r.method === 'PUT');
      expect(req.request.body.reason).toBe('Changed');
      req.flush({
        id: 'blackout-1', resourceId: 'resource-1', startUtc: '2026-01-01T00:00:00Z', endUtc: '2026-01-02T00:00:00Z',
        reason: 'Changed', conflictingBookingIds: [],
      });
    });

    it('deleteBlackoutPeriod DELETEs the specific blackout id under the resource', () => {
      service.deleteBlackoutPeriod('resource-1', 'blackout-1').subscribe();

      httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/blackout-periods/blackout-1` && r.method === 'DELETE').flush(null);
    });
  });

  describe('resource approvers', () => {
    it('getAssignedApprovers resolves each bare assignment into display data via GET /users?ids=', () => {
      let result: unknown;
      service.getAssignedApprovers('resource-1').subscribe((approvers) => (result = approvers));

      httpTesting.expectOne(`${RESOURCE_URL}/approvers`).flush([{ id: 'assignment-1', resourceId: 'resource-1', userId: 'user-1' }]);
      const usersReq = httpTesting.expectOne((r) => r.url === USERS_URL);
      expect(usersReq.request.params.getAll('ids')).toEqual(['user-1']);
      usersReq.flush({
        items: [{ id: 'user-1', firstName: 'Jane', lastName: 'Doe', email: 'jane@example.com', tenantId: 'tenant-1', status: 0, roles: ['Approver'] }],
        page: 1, pageSize: 1, totalCount: 1,
      });

      expect(result).toEqual([
        { assignmentId: 'assignment-1', userId: 'user-1', resolution: 'resolved', firstName: 'Jane', lastName: 'Doe', email: 'jane@example.com', roles: ['Approver'] },
      ]);
    });

    it('getAssignedApprovers never calls GET /users when there are no assignments', () => {
      let result: unknown;
      service.getAssignedApprovers('resource-1').subscribe((approvers) => (result = approvers));

      httpTesting.expectOne(`${RESOURCE_URL}/approvers`).flush([]);

      expect(result).toEqual([]);
      httpTesting.expectNone((r) => r.url === USERS_URL);
    });

    it('assignResourceApprover POSTs the userId under the resource', () => {
      service.assignResourceApprover('resource-1', { userId: 'user-1' }).subscribe();

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/approvers` && r.method === 'POST');
      expect(req.request.body).toEqual({ userId: 'user-1' });
      req.flush({ id: 'assignment-1', resourceId: 'resource-1', userId: 'user-1' });
    });

    it('removeResourceApprover DELETEs the specific user id under the resource', () => {
      service.removeResourceApprover('resource-1', 'user-1').subscribe();

      httpTesting.expectOne((r) => r.url === `${RESOURCE_URL}/approvers/user-1` && r.method === 'DELETE').flush(null);
    });
  });

  describe('resource types', () => {
    it('createResourceType POSTs the name and adds the created type into the shared resourceTypes signal, sorted by name', () => {
      let result: unknown;
      service.createResourceType({ name: 'Desk' }).subscribe((type) => (result = type));

      const req = httpTesting.expectOne((r) => r.url === TYPES_URL && r.method === 'POST');
      expect(req.request.body).toEqual({ name: 'Desk' });
      req.flush({ id: 'type-1', name: 'Desk' });

      expect(result).toEqual({ id: 'type-1', name: 'Desk' });
      expect(service.resourceTypes()).toEqual([{ id: 'type-1', name: 'Desk' }]);
    });

    it('updateResourceType PUTs to the type id and replaces the matching entry in the shared signal', () => {
      service.createResourceType({ name: 'Desk' }).subscribe();
      httpTesting.expectOne((r) => r.url === TYPES_URL && r.method === 'POST').flush({ id: 'type-1', name: 'Desk' });

      service.updateResourceType('type-1', { name: 'Standing Desk' }).subscribe();
      httpTesting.expectOne((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'PUT').flush({ id: 'type-1', name: 'Standing Desk' });

      expect(service.resourceTypes()).toEqual([{ id: 'type-1', name: 'Standing Desk' }]);
    });

    it('deleteResourceType DELETEs the type id and removes it from the shared signal', () => {
      service.createResourceType({ name: 'Desk' }).subscribe();
      httpTesting.expectOne((r) => r.url === TYPES_URL && r.method === 'POST').flush({ id: 'type-1', name: 'Desk' });

      service.deleteResourceType('type-1').subscribe();
      httpTesting.expectOne((r) => r.url === `${TYPES_URL}/type-1` && r.method === 'DELETE').flush(null);

      expect(service.resourceTypes()).toEqual([]);
    });

    it('reloadResourceTypes always issues a fresh request, unlike getResourceTypes\' cached shareReplay', () => {
      service.getResourceTypes().subscribe();
      httpTesting.expectOne(TYPES_URL).flush([{ id: 'type-1', name: 'Desk' }]);

      service.reloadResourceTypes().subscribe();
      httpTesting.expectOne(TYPES_URL).flush([{ id: 'type-1', name: 'Desk' }, { id: 'type-2', name: 'Room' }]);

      expect(service.resourceTypes()).toEqual([{ id: 'type-1', name: 'Desk' }, { id: 'type-2', name: 'Room' }]);
    });
  });

  describe('supported time zones', () => {
    it('getSupportedTimeZones GETs the canonical list from the backend', () => {
      let result: unknown;
      service.getSupportedTimeZones().subscribe((zones) => (result = zones));

      httpTesting.expectOne(`${RESOURCES_URL}/supported-timezones`).flush(['Europe/Sarajevo', 'UTC']);

      expect(result).toEqual(['Europe/Sarajevo', 'UTC']);
    });

    it('caches the list for the app\'s lifetime - a second call issues no further request', () => {
      service.getSupportedTimeZones().subscribe();
      httpTesting.expectOne(`${RESOURCES_URL}/supported-timezones`).flush(['UTC']);

      let second: unknown;
      service.getSupportedTimeZones().subscribe((zones) => (second = zones));

      httpTesting.expectNone(`${RESOURCES_URL}/supported-timezones`);
      expect(second).toEqual(['UTC']);
    });

    it('does not cache a failed request - the next call retries with a fresh HTTP request', () => {
      let firstErrored = false;
      service.getSupportedTimeZones().subscribe({ error: () => (firstErrored = true) });
      httpTesting.expectOne(`${RESOURCES_URL}/supported-timezones`).flush('boom', { status: 500, statusText: 'Internal Server Error' });
      expect(firstErrored).toBe(true);

      let second: unknown;
      service.getSupportedTimeZones().subscribe((zones) => (second = zones));
      httpTesting.expectOne(`${RESOURCES_URL}/supported-timezones`).flush(['UTC']);

      expect(second).toEqual(['UTC']);
    });
  });
});
