import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { environment } from '../environments/environment';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { ResourceDetailComponent } from './features/resources/resource-detail/resource-detail';
import { ShellComponent } from './features/shell/shell';
import { UserListComponent } from './features/users/user-list/user-list';

const RESOURCE_ID = 'resource-1';

function dragstartOn(el: Element): Event {
  const event = new Event('dragstart', { bubbles: true, cancelable: true });
  el.dispatchEvent(event);
  return event;
}

// Integration coverage for App.onInternalLinkDragStart (app.ts): proves the centralized, app-root-level
// dragstart handler actually reaches real, fully-rendered pages mounted under the real <router-outlet> -
// not just the synthetic anchors built by hand in app.spec.ts's edge-case coverage. This closes the loop
// with the original bug report, which was found on a real page (the sidebar), then reproduced on another
// real page (the Users list), not on a hand-built anchor.
describe('centralized internal-link native-drag prevention reaches real rendered pages', () => {
  let httpTesting: HttpTestingController;

  afterEach(() => httpTesting.verify());

  it('prevents dragstart on the real, rendered Users list row (the reported freeze)', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'users', component: UserListComponent }]),
        {
          provide: AuthService,
          useValue: { hasAnyRole: () => true, currentUser: () => ({ userId: 'admin-1', email: 'admin@bookspace.test', tenantId: 'tenant-1', roles: [] }) },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl('/users');
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/users`).flush({
      items: [{ id: 'user-1', firstName: 'Jane', lastName: 'Doe', email: 'jane@example.com', tenantId: 'tenant-1', status: 'Active', roles: [] }],
      page: 1,
      pageSize: 20,
      totalCount: 1,
    });
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('a.user-row') as HTMLAnchorElement | null;
    expect(row).toBeTruthy();
    expect(row!.getAttribute('href')).toBe('/users/user-1');

    expect(dragstartOn(row!).defaultPrevented).toBe(true);
  });

  it('prevents dragstart on the real, rendered Resource detail page\'s "Back to resources" link', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'resources/:id', component: ResourceDetailComponent }]),
        { provide: AuthService, useValue: { hasAnyRole: () => false } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl(`/resources/${RESOURCE_ID}`);
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resource-types`).flush([{ id: 'type-1', name: 'Meeting Room' }]);
    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/resources/${RESOURCE_ID}` && r.method === 'GET').flush({
      id: RESOURCE_ID,
      resourceTypeId: 'type-1',
      name: 'Falcon Room',
      description: 'A quiet room',
      capacity: 4,
      requiresApproval: false,
      status: 0,
      timeZoneId: 'UTC',
    });
    fixture.detectChanges();

    const backLink = (fixture.nativeElement as HTMLElement).querySelector('a.resource-detail__back') as HTMLAnchorElement | null;
    expect(backLink).toBeTruthy();
    expect(backLink!.getAttribute('href')).toBe('/resources');

    expect(dragstartOn(backLink!).defaultPrevented).toBe(true);
  });

  it('prevents dragstart on a real, rendered sidebar nav link', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '', component: ShellComponent, children: [] }]),
        {
          provide: AuthService,
          useValue: {
            hasAnyRole: () => true,
            currentUser: () => ({ userId: 'admin-1', email: 'admin@bookspace.test', tenantId: 'tenant-1', roles: [] }),
            displayName: () => 'Admin',
            logout: () => undefined,
          },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl('/');
    fixture.detectChanges();

    httpTesting.expectOne((r) => r.url === `${environment.apiUrl}/bookings/pending-approval`).flush([]);
    fixture.detectChanges();

    const sidebarLink = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a.shell-nav__link')].find(
      (a) => a.getAttribute('href') === '/resources',
    ) as HTMLAnchorElement | undefined;
    expect(sidebarLink).toBeTruthy();

    expect(dragstartOn(sidebarLink!).defaultPrevented).toBe(true);
  });
});
