import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceFormComponent } from './resource-form';

const RESOURCES_URL = `${environment.apiUrl}/resources`;
const TYPES_URL = `${environment.apiUrl}/resource-types`;

function wireResource(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'resource-1',
    resourceTypeId: 'type-1',
    name: 'Falcon Room',
    description: 'A quiet room',
    capacity: 4,
    requiresApproval: false,
    status: 0, // ResourceStatus.Active
    timeZoneId: 'UTC',
    ...overrides,
  };
}

describe('ResourceFormComponent', () => {
  let fixture: ComponentFixture<ResourceFormComponent>;
  let httpTesting: HttpTestingController;
  let router: Router;

  function configure(resourceId: string | null): void {
    TestBed.configureTestingModule({
      imports: [ResourceFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => resourceId } } } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(ResourceFormComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function setInput(id: string, value: string): void {
    const input = root().querySelector(`#${id}`) as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;
    input.value = value;
    input.dispatchEvent(new Event(id === 'resource-type' || id === 'status' ? 'change' : 'input'));
  }

  function submitButton(): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.getAttribute('type') === 'submit') as HTMLButtonElement;
  }

  afterEach(() => httpTesting.verify());

  describe('create mode (resources/new)', () => {
    beforeEach(() => {
      configure(null);
      httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Meeting Room' }]);
      fixture.detectChanges();
    });

    it('renders an empty form with no resource fetch', () => {
      expect(root().textContent).toContain('New resource');
      httpTesting.expectNone((r) => r.url.startsWith(`${RESOURCES_URL}/`));
    });

    it('blocks submission client-side when required fields are missing, without an HTTP call', () => {
      submitButton().click();
      fixture.detectChanges();

      expect(root().textContent).toContain('Please select a resource type');
      httpTesting.expectNone((r) => r.url === RESOURCES_URL);
    });

    it('submits a valid new resource, shows a success toast, and navigates to its detail page', () => {
      setInput('resource-type', 'type-1');
      setInput('name', 'Falcon Room');
      setInput('capacity', '4');
      setInput('time-zone', 'UTC');
      submitButton().click();

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
      fixture.detectChanges();

      expect(router.navigate).toHaveBeenCalledWith(['/resources', 'resource-1']);
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Resource created.' })]);
    });

    it('renders field-level errors returned by the API without touching the generic form error', () => {
      setInput('resource-type', 'type-1');
      setInput('name', 'Falcon Room');
      setInput('capacity', '4');
      setInput('time-zone', 'Not/AZone');
      submitButton().click();

      httpTesting.expectOne((r) => r.url === RESOURCES_URL && r.method === 'POST').flush(
        { title: 'Validation failed', errors: { TimeZoneId: ["'TimeZoneId' is not a recognized time zone identifier."] } },
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(root().textContent).toContain('is not a recognized time zone identifier');
      expect(root().querySelector('.banner-error')).toBeNull();
    });

    it('shows a friendly message for a duplicate resource name conflict', () => {
      setInput('resource-type', 'type-1');
      setInput('name', 'Falcon Room');
      setInput('capacity', '4');
      setInput('time-zone', 'UTC');
      submitButton().click();

      httpTesting.expectOne((r) => r.url === RESOURCES_URL && r.method === 'POST').flush(
        { title: 'Conflict', detail: "A resource named 'Falcon Room' already exists.", errorCode: 'Resource.NameConflict' },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(root().textContent).toContain('A resource with this name already exists.');
    });
  });

  describe('edit mode (resources/:id/edit)', () => {
    beforeEach(() => {
      configure('resource-1');
      httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Meeting Room' }]);
    });

    it('loads and prefills the existing resource, including its current status', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(wireResource({ status: 1 })); // Inactive
      fixture.detectChanges();

      expect((root().querySelector('#name') as HTMLInputElement).value).toBe('Falcon Room');
      expect((root().querySelector('#capacity') as HTMLInputElement).value).toBe('4');
      expect((root().querySelector('#status') as HTMLSelectElement).value).toBe('Inactive');
    });

    it('submits an update as a PUT with the status sent as its numeric wire code', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(wireResource());
      fixture.detectChanges();

      setInput('name', 'Falcon Room (renamed)');
      setInput('status', 'Maintenance');
      submitButton().click();

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT');
      expect(req.request.body.name).toBe('Falcon Room (renamed)');
      expect(req.request.body.status).toBe(2); // ResourceStatus.Maintenance
      req.flush(wireResource({ name: 'Falcon Room (renamed)', status: 2 }));
      fixture.detectChanges();

      expect(router.navigate).toHaveBeenCalledWith(['/resources', 'resource-1']);
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Resource updated.' })]);
    });

    it('shows a not-found state for an unknown resource id, instead of an empty form', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(
        { title: 'Not Found' },
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(root().textContent).toContain("This resource doesn't exist");
      expect(root().querySelector('form')).toBeNull();
    });

    it('surfaces the backend\'s own message when submitting an edit to an already-archived resource', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(wireResource({ status: 3 })); // Archived
      fixture.detectChanges();
      submitButton().click();

      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT').flush(
        { title: 'Conflict', detail: 'Resource resource-1 is archived and cannot be modified.' },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(root().textContent).toContain('is archived and cannot be modified');
    });
  });
});
