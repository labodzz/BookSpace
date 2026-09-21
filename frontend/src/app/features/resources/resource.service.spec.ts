import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ResourceService } from './resource.service';

const RESOURCES_URL = `${environment.apiUrl}/resources`;

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
});
