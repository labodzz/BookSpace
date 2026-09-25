import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { NotificationService } from '../../../core/notifications/notification.service';
import { ResourceFormComponent } from './resource-form';

const RESOURCES_URL = `${environment.apiUrl}/resources`;
const TYPES_URL = `${environment.apiUrl}/resource-types`;
const TIMEZONES_URL = `${environment.apiUrl}/resources/supported-timezones`;
const SUPPORTED_TIMEZONES = ['America/New_York', 'Asia/Tokyo', 'Europe/London', 'Europe/Sarajevo', 'UTC'];

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
    // Deterministic across machines/CI - TimezoneSelectComponent's Create-mode default detection reads
    // this real API, so it's pinned here rather than left to whatever the test host's own system time
    // zone happens to be. 'Europe/Sarajevo' is deliberately included in SUPPORTED_TIMEZONES above.
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
      timeZone: 'Europe/Sarajevo',
    } as Intl.ResolvedDateTimeFormatOptions);

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

  // Mirrors real combobox interaction (see timezone-select.ts): focus opens the list, typing filters
  // it, and only a mousedown on the matching option actually commits a selection - the same mechanism
  // timezone-select.spec.ts exercises directly and in more depth; this just proves the form wires it up.
  function selectTimeZone(zoneId: string): void {
    const input = root().querySelector('#time-zone') as HTMLInputElement;
    input.dispatchEvent(new Event('focus'));
    fixture.detectChanges();
    input.value = zoneId;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    const option = [...root().querySelectorAll('[role="option"]')].find((element) => element.textContent?.trim() === zoneId);
    option?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  function submitButton(): HTMLButtonElement {
    return [...root().querySelectorAll('button')].find((b) => b.getAttribute('type') === 'submit') as HTMLButtonElement;
  }

  // TimezoneSelectComponent is a CHILD component only constructed once its host template actually
  // renders it - not at TestBed.createComponent() time like ResourceFormComponent's own constructor.
  // In edit mode that's only once loadingResource() flips false (i.e. after a detectChanges() that
  // follows the resource GET being flushed), so callers must flush that first - see each edit-mode
  // test below.
  function flushTimeZones(): void {
    httpTesting.expectOne((r) => r.url === TIMEZONES_URL).flush(SUPPORTED_TIMEZONES);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpTesting.verify();
    vi.restoreAllMocks();
  });

  describe('create mode (resources/new)', () => {
    beforeEach(() => {
      configure(null);
      httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Meeting Room' }]);
      fixture.detectChanges(); // constructs app-timezone-select, which issues the TIMEZONES_URL request
      flushTimeZones();
    });

    it('renders an empty form with no resource fetch', () => {
      expect(root().textContent).toContain('New resource');
      httpTesting.expectNone((r) => r.url.startsWith(`${RESOURCES_URL}/`));
    });

    it('preselects the (mocked) browser time zone as the default, since it is in the supported list', () => {
      expect((root().querySelector('#time-zone') as HTMLInputElement).value).toBe('Europe/Sarajevo');
    });

    it('blocks submission client-side when required fields are missing, without an HTTP call', () => {
      submitButton().click();
      fixture.detectChanges();

      expect(root().textContent).toContain('Please select a resource type');
      httpTesting.expectNone((r) => r.url === RESOURCES_URL);
    });

    it('submits a valid new resource with the selected time zone, shows a success toast, and navigates to its detail page', () => {
      setInput('resource-type', 'type-1');
      setInput('name', 'Falcon Room');
      setInput('capacity', '4');
      selectTimeZone('UTC');
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
      // A new resource comes back Inactive (no AvailabilityRule yet) - see CreateResourceCommandHandler.
      req.flush(wireResource({ status: 1 }));
      fixture.detectChanges();

      // Sent straight to setup, not the detail page - see resource-form.ts's create-mode navigation.
      expect(router.navigate).toHaveBeenCalledWith(['/resources', 'resource-1', 'manage-availability']);
      expect(TestBed.inject(NotificationService).toasts()).toEqual([expect.objectContaining({ message: 'Resource created.' })]);
    });

    it('renders field-level errors the backend returns for the submitted time zone', () => {
      setInput('resource-type', 'type-1');
      setInput('name', 'Falcon Room');
      setInput('capacity', '4');
      selectTimeZone('Europe/London');
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
      selectTimeZone('UTC');
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

    it('loads and prefills the existing resource, including its current status and its own time zone - never the browser default', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(
        wireResource({ status: 1, timeZoneId: 'Asia/Tokyo' }), // Inactive
      );
      fixture.detectChanges();
      flushTimeZones();

      expect((root().querySelector('#name') as HTMLInputElement).value).toBe('Falcon Room');
      expect((root().querySelector('#capacity') as HTMLInputElement).value).toBe('4');
      expect((root().querySelector('#status') as HTMLSelectElement).value).toBe('Inactive');
      // Not 'Europe/Sarajevo' - configure() above mocks that as the browser's own zone, and it must
      // never override an Edit resource's already-stored value.
      expect((root().querySelector('#time-zone') as HTMLInputElement).value).toBe('Asia/Tokyo');
    });

    it('submits an update as a PUT with the status sent as its numeric wire code, keeping the resource\'s own time zone', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(wireResource());
      fixture.detectChanges();
      flushTimeZones();

      setInput('name', 'Falcon Room (renamed)');
      setInput('status', 'Maintenance');
      submitButton().click();

      const req = httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT');
      expect(req.request.body.name).toBe('Falcon Room (renamed)');
      expect(req.request.body.status).toBe(2); // ResourceStatus.Maintenance
      expect(req.request.body.timeZoneId).toBe('UTC');
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
      flushTimeZones();
      submitButton().click();

      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'PUT').flush(
        { title: 'Conflict', detail: 'Resource resource-1 is archived and cannot be modified.' },
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(root().textContent).toContain('is archived and cannot be modified');
    });

    it('displays a legacy stored time zone that is not in the supported list, with a warning, rather than clearing it', () => {
      httpTesting.expectOne((r) => r.url === `${RESOURCES_URL}/resource-1` && r.method === 'GET').flush(
        wireResource({ timeZoneId: 'Eastern Standard Time' }),
      );
      fixture.detectChanges();
      flushTimeZones();

      expect((root().querySelector('#time-zone') as HTMLInputElement).value).toBe('Eastern Standard Time');
      expect(root().textContent).toContain("isn't in the current supported list");
    });
  });
});
