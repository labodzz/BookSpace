import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { AuthService } from '../../../core/auth/auth.service';
import { ResourceListComponent } from './resource-list';

const TYPES_URL = `${environment.apiUrl}/resource-types`;
const RESOURCES_URL = `${environment.apiUrl}/resources`;

describe('ResourceListComponent', () => {
  let fixture: ComponentFixture<ResourceListComponent>;
  let httpTesting: HttpTestingController;

  function configure(hasAnyRole: (...roles: string[]) => boolean = () => false): void {
    TestBed.configureTestingModule({
      imports: [ResourceListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: { hasAnyRole } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    // The component fires its initial requests from its constructor, so create it only after the
    // testing controller is ready to intercept them.
    fixture = TestBed.createComponent(ResourceListComponent);
  }

  afterEach(() => httpTesting.verify());

  it('renders each returned resource with its resolved type name', () => {
    configure();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Meeting Room' }]);
    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({
      items: [
        {
          id: 'resource-1',
          resourceTypeId: 'type-1',
          name: 'Falcon Room',
          description: null,
          capacity: 4,
          requiresApproval: false,
          status: 0, // ResourceStatus.Active - the API serializes enums as raw numbers, not names
          timeZoneId: 'UTC',
        },
      ],
      page: 1,
      pageSize: 12,
      totalCount: 1,
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Falcon Room');
    expect(text).toContain('Meeting Room');
  });

  it('shows an empty state when there are no resources for the current filters', () => {
    configure();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([]);
    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({ items: [], page: 1, pageSize: 12, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No resources found');
  });

  it('shows a retry affordance on a failed load, and retry re-issues the request', () => {
    configure();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([]);
    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain("Couldn't load resources");

    const retryButton = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((button) =>
      button.textContent?.includes('Try again'),
    ) as HTMLButtonElement;
    retryButton.click();

    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({ items: [], page: 1, pageSize: 12, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No resources found');
  });

  // getResourceTypes() is a shared, cached request with no built-in catchError - a bare
  // `.subscribe()` with no error handler would make a failure here an unhandled RxJS error (thrown
  // synchronously out of flush() below) instead of just leaving type names unresolved.
  it('does not throw when the resource-types request fails, and still renders the resources list', () => {
    configure();
    expect(() => httpTesting.expectOne((r) => r.url === TYPES_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' })).not.toThrow();

    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({
      items: [
        {
          id: 'resource-1',
          resourceTypeId: 'type-1',
          name: 'Falcon Room',
          description: null,
          capacity: 4,
          requiresApproval: false,
          status: 0,
          timeZoneId: 'UTC',
        },
      ],
      page: 1,
      pageSize: 12,
      totalCount: 1,
    });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Falcon Room');
  });

  // "New resource" is TenantAdmin/SysAdmin-only - a Member browsing to book something should never see
  // an entry point into resource administration.
  it('shows the "New resource" action for a TenantAdmin', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([]);
    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({ items: [], page: 1, pageSize: 12, totalCount: 0 });
    fixture.detectChanges();

    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].find((a) => a.textContent?.trim() === 'New resource');
    expect(link).not.toBeUndefined();
    expect(link?.getAttribute('href')).toBe('/resources/new');
  });

  it('hides the "New resource" action for a plain Member', () => {
    configure();
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([]);
    httpTesting.expectOne((r) => r.url === RESOURCES_URL).flush({ items: [], page: 1, pageSize: 12, totalCount: 0 });
    fixture.detectChanges();

    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].find((a) => a.textContent?.trim() === 'New resource');
    expect(link).toBeUndefined();
  });
});
