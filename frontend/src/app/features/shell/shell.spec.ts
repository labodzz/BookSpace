import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';
import { ShellComponent } from './shell';

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let httpTesting: HttpTestingController;
  let logout: ReturnType<typeof vi.fn>;

  function configure(currentUser: { email: string } | null): void {
    logout = vi.fn();
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { hasAnyRole: () => true, currentUser: () => currentUser, displayName: () => 'Ana', logout },
        },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShellComponent);
  }

  function flushPendingApprovals(): void {
    httpTesting.expectOne(`${environment.apiUrl}/bookings/pending-approval`).flush([]);
  }

  afterEach(() => httpTesting.verify());

  describe('with no signed-in user (defensive/loading case)', () => {
    beforeEach(() => configure(null));

    // getPendingApprovals().subscribe(...) used to pass only a next callback - a failure here would
    // have been an unhandled RxJS error thrown synchronously out of flush() below, instead of just
    // leaving the approvals nav badge at its initial 0.
    it('does not throw when the pending-approvals request fails, and leaves the badge count at 0', () => {
      expect(() =>
        httpTesting.expectOne(`${environment.apiUrl}/bookings/pending-approval`).flush('boom', { status: 500, statusText: 'Internal Server Error' }),
      ).not.toThrow();

      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('.shell-nav__badge')).toBeNull();
    });

    it('still shows the theme toggle in the sidebar footer even before the user resolves', () => {
      flushPendingApprovals();
      fixture.detectChanges();

      const footer = (fixture.nativeElement as HTMLElement).querySelector('.shell-sidebar__footer');
      expect(footer?.querySelector('app-theme-toggle')).not.toBeNull();
    });
  });

  describe('with a signed-in user', () => {
    beforeEach(() => {
      configure({ email: 'ana@example.com' });
      flushPendingApprovals();
      fixture.detectChanges();
    });

    function root(): HTMLElement {
      return fixture.nativeElement as HTMLElement;
    }

    it('renders the BookSpace logo/wordmark as a link to the dashboard', () => {
      const brand = root().querySelector('a.shell-sidebar__brand') as HTMLAnchorElement | null;

      expect(brand).not.toBeNull();
      expect(brand!.querySelector('svg.shell-sidebar__logo')).not.toBeNull();
      expect(brand!.textContent).toContain('BookSpace');
      expect(brand!.getAttribute('href')).toBe('/');
    });

    it('puts the user info, theme toggle, and Log out all inside the sidebar footer, in that order', () => {
      const footer = root().querySelector('.shell-sidebar__footer') as HTMLElement;
      expect(footer).not.toBeNull();

      const children = [...footer.children];
      const userIndex = children.findIndex((el) => el.classList.contains('shell-sidebar__user'));
      const toggleIndex = children.findIndex((el) => el.tagName.toLowerCase() === 'app-theme-toggle');
      const logoutIndex = children.findIndex((el) => el.classList.contains('shell-sidebar__logout'));

      expect(userIndex).toBeGreaterThanOrEqual(0);
      expect(toggleIndex).toBeGreaterThan(userIndex);
      expect(logoutIndex).toBeGreaterThan(toggleIndex);
      expect(footer.textContent).toContain('ana@example.com');
    });

    it('keeps the sidebar and the main content as separate structural containers', () => {
      const shell = root().querySelector('.shell') as HTMLElement;
      const sidebar = shell.querySelector(':scope > .shell-sidebar');
      const content = shell.querySelector(':scope > main.shell-content');

      expect(sidebar).not.toBeNull();
      expect(content).not.toBeNull();
      // The sidebar's own nav/footer must live inside .shell-sidebar, never inside .shell-content -
      // otherwise the independent-scroll regions in shell.scss would not correspond to this markup.
      expect(content!.contains(sidebar)).toBe(false);
      expect(sidebar!.contains(content)).toBe(false);
    });

    it('still logs out and navigates to /welcome when Log out is clicked', () => {
      const logoutButton = root().querySelector('.shell-sidebar__logout') as HTMLButtonElement;
      const router = TestBed.inject(Router);
      const navigateSpy = vi.spyOn(router, 'navigate');

      logoutButton.click();

      expect(logout).toHaveBeenCalledTimes(1);
      expect(navigateSpy).toHaveBeenCalledWith(['/welcome']);
    });
  });
});
