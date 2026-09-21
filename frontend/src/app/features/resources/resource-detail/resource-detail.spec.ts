import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { environment } from '../../../../environments/environment';
import { AuthService } from '../../../core/auth/auth.service';
import { ResourceDetailComponent } from './resource-detail';

const RESOURCE_ID = 'resource-1';
const RESOURCE_URL = `${environment.apiUrl}/resources/${RESOURCE_ID}`;
const TYPES_URL = `${environment.apiUrl}/resource-types`;

function wireResource(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: RESOURCE_ID,
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

describe('ResourceDetailComponent', () => {
  let fixture: ComponentFixture<ResourceDetailComponent>;
  let httpTesting: HttpTestingController;

  function configure(hasAnyRole: (...roles: string[]) => boolean = () => false): void {
    TestBed.configureTestingModule({
      imports: [ResourceDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => RESOURCE_ID } } } },
        { provide: AuthService, useValue: { hasAnyRole } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ResourceDetailComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushLoad(overrides: Partial<Record<string, unknown>> = {}): void {
    httpTesting.expectOne((r) => r.url === TYPES_URL).flush([{ id: 'type-1', name: 'Meeting Room' }]);
    httpTesting.expectOne((r) => r.url === RESOURCE_URL && r.method === 'GET').flush(wireResource(overrides));
    fixture.detectChanges();
  }

  afterEach(() => httpTesting.verify());

  it('does not show Edit/Archive controls for a plain Member', () => {
    configure();
    flushLoad();

    expect(root().textContent).not.toContain('Archive');
    expect([...root().querySelectorAll('a')].some((a) => a.textContent?.trim() === 'Edit')).toBe(false);
  });

  it('shows Edit/Archive controls for a TenantAdmin on an active resource', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad();

    const editLink = [...root().querySelectorAll('a')].find((a) => a.textContent?.trim() === 'Edit');
    expect(editLink?.getAttribute('href')).toBe(`/resources/${RESOURCE_ID}/edit`);
    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Archive')).toBe(true);
  });

  it('hides Edit/Archive controls for an already-archived resource, even for a TenantAdmin', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad({ status: 3 }); // ResourceStatus.Archived

    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Archive')).toBe(false);
    expect([...root().querySelectorAll('a')].some((a) => a.textContent?.trim() === 'Edit')).toBe(false);
  });

  it('opens a confirmation dialog before archiving, and does nothing until confirmed', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad();

    const archiveButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive') as HTMLButtonElement;
    archiveButton.click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().textContent).toContain('Archiving is permanent');
    httpTesting.expectNone((r) => r.url === RESOURCE_URL && r.method === 'DELETE');
  });

  it('archives the resource on confirm, closes the dialog, and reflects the new status', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad();

    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive resource') as HTMLButtonElement
    ).click();

    httpTesting.expectOne((r) => r.url === RESOURCE_URL && r.method === 'DELETE').flush(wireResource({ status: 3 }));
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    expect(root().textContent).toContain('Archived');
  });

  it('keeps the dialog open and shows an error message if archiving fails', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad();

    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive resource') as HTMLButtonElement
    ).click();

    httpTesting
      .expectOne((r) => r.url === RESOURCE_URL && r.method === 'DELETE')
      .flush({ title: 'Conflict', detail: 'Something went wrong.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).not.toBeNull();
    expect(root().querySelector('.dialog .banner-error')).not.toBeNull();
  });

  it('closes the dialog without archiving when "Keep resource" is clicked', () => {
    configure((...roles) => roles.includes('TenantAdmin'));
    flushLoad();

    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Archive') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    (
      [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Keep resource') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(root().querySelector('.dialog')).toBeNull();
    httpTesting.expectNone((r) => r.url === RESOURCE_URL && r.method === 'DELETE');
  });
});
