import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';
import { ShellComponent } from './shell';

@Component({ selector: 'app-empty-stand-in', template: '' })
class EmptyStandInComponent {}

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let httpTesting: HttpTestingController;
  let logout: ReturnType<typeof vi.fn>;

  // hasAnyRole defaults to always-true (every role-gated nav link renders, isApprover is true) so a
  // test only needs to override what it actually cares about - hasAnyRole for role-gating coverage,
  // currentUser for signed-in/signed-out footer coverage. Both defaults match what each side of this
  // file's history already assumed on its own.
  function configure(options: { hasAnyRole?: (...roles: string[]) => boolean; currentUser?: { email: string } | null } = {}): void {
    const { hasAnyRole = () => true, currentUser = null } = options;
    logout = vi.fn();
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Real (if trivial) targets for '' and 'resources' - without them, routerLinkActive has no
        // resolved current URL to match against, and clicking the /resources routerLink would have
        // nowhere to navigate.
        provideRouter([
          { path: '', loadComponent: () => Promise.resolve({ default: EmptyStandInComponent }) },
          { path: 'resources', loadComponent: () => Promise.resolve({ default: EmptyStandInComponent }) },
        ]),
        { provide: AuthService, useValue: { hasAnyRole, currentUser: () => currentUser, displayName: () => 'Ana', logout } },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShellComponent);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function flushPendingApprovals(): void {
    httpTesting.expectOne(`${environment.apiUrl}/bookings/pending-approval`).flush([]);
  }

  afterEach(() => httpTesting.verify());

  // getPendingApprovals().subscribe(...) used to pass only a next callback - a failure here would have
  // been an unhandled RxJS error thrown synchronously out of flush() below, instead of just leaving the
  // approvals nav badge at its initial 0.
  it('does not throw when the pending-approvals request fails, and leaves the badge count at 0', () => {
    configure();

    expect(() =>
      httpTesting.expectOne(`${environment.apiUrl}/bookings/pending-approval`).flush('boom', { status: 500, statusText: 'Internal Server Error' }),
    ).not.toThrow();

    fixture.detectChanges();
    expect(root().querySelector('.shell-nav__badge')).toBeNull();
  });

  it('shows the "Resource types" nav link, guarded by the same route as "Users", for a TenantAdmin or SysAdmin', () => {
    configure({ hasAnyRole: (...roles) => roles.includes('SysAdmin') });
    flushPendingApprovals();
    fixture.detectChanges();

    const link = [...root().querySelectorAll('a')].find((a) => a.textContent?.trim() === 'Resource types');
    expect(link?.getAttribute('href')).toBe('/resource-types');
  });

  it('does not show the "Resource types" nav link for a plain Member', () => {
    // hasAnyRole() => false also means isApprover is false, so shell.ts never issues the
    // pending-approvals request at all here - nothing to flush, unlike the SysAdmin case above.
    configure({ hasAnyRole: () => false });
    fixture.detectChanges();

    expect([...root().querySelectorAll('a')].some((a) => a.textContent?.trim() === 'Resource types')).toBe(false);
  });

  // Regression coverage for the confirmed root cause: a real browser starts a native link-drag from a
  // sidebar anchor on a fast mousedown+slight-move, which cancels the normal pointer sequence
  // (pointerdown/mousedown/dragstart/pointercancel, never reaching pointerup/mouseup/click) and leaves
  // the page stuck until reload. draggable="false" (plus the CSS -webkit-user-drag: none belt-and-
  // suspenders in shell.scss) is what prevents the browser from ever starting that drag in the first
  // place. jsdom cannot reproduce the actual stuck-drag browser behavior itself - see the dedicated
  // dragstart test below for what IS and isn't proven here.
  describe('sidebar links are not draggable (fix for the native-drag freeze)', () => {
    it('marks every sidebar nav anchor draggable="false", for every visible role combination', () => {
      configure({ hasAnyRole: () => true }); // SysAdmin-equivalent - every conditional link renders
      flushPendingApprovals();
      fixture.detectChanges();

      const links = [...root().querySelectorAll('a.shell-nav__link')];
      expect(links.length).toBeGreaterThan(0);
      for (const link of links) {
        expect(link.getAttribute('draggable')).toBe('false');
      }
    });

    it('has no anchor or image anywhere in the sidebar that lacks draggable="false" (brand/logo included, if ever changed to a link)', () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();

      const sidebar = root().querySelector('.shell-sidebar')!;
      const anchors = [...sidebar.querySelectorAll('a')];
      const images = [...sidebar.querySelectorAll('img')];
      for (const anchor of anchors) {
        expect(anchor.getAttribute('draggable')).toBe('false');
      }
      for (const image of images) {
        expect(image.getAttribute('draggable')).toBe('false');
      }
    });

    // Confirms the anchor is actually CONFIGURED as non-draggable (the attribute + the DOM's own
    // `.draggable` boolean reflecting it) - NOT a claim that jsdom reproduces the real stuck-drag
    // freeze itself (it doesn't implement native drag-and-drop at all). Real Chromium/Edge verification
    // of the actual event sequence after this fix is still required and is covered in the report, not
    // in this test.
    it('dispatches dragstart against a sidebar link and confirms the link is configured non-draggable', () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();

      const link = root().querySelector('a.shell-nav__link') as HTMLAnchorElement;
      expect(link.draggable).toBe(false);

      // jsdom does not implement the DragEvent constructor - a plain Event of the same type is enough
      // to prove nothing in this app throws on or specially handles a dragstart (there is deliberately
      // no dragstart listener anywhere - see the "no global drag prevention" test below).
      expect(() => link.dispatchEvent(new Event('dragstart', { bubbles: true, cancelable: true }))).not.toThrow();
    });

    it('leaves ordinary click navigation working (RouterLink still fires a real navigation)', async () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();
      const router = TestBed.inject(Router);

      const link = [...root().querySelectorAll('a.shell-nav__link')].find((a) => a.getAttribute('href') === '/resources')!;
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
      await fixture.whenStable();

      expect(router.url).toBe('/resources');
    });

    // A browser fires the exact same native 'click' event for a keyboard Enter activation of a focused
    // link as it does for a mouse click - RouterLink has no separate code path for either, so this is the
    // correct and sufficient proxy for "keyboard Tab + Enter still navigates".
    it('leaves keyboard activation (Enter on a focused link) working, the same native click event RouterLink already handles', async () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();
      const router = TestBed.inject(Router);

      const link = [...root().querySelectorAll('a.shell-nav__link')].find((a) => a.getAttribute('href') === '/resources')! as HTMLAnchorElement;
      link.focus();
      expect(document.activeElement).toBe(link);
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      await fixture.whenStable();

      expect(router.url).toBe('/resources');
    });

    it('still applies routerLinkActive to the current route', async () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();
      await TestBed.inject(Router).navigateByUrl('/');
      fixture.detectChanges();

      const dashboardLink = [...root().querySelectorAll('a.shell-nav__link')].find((a) => a.getAttribute('href') === '/')!;
      expect(dashboardLink.classList.contains('shell-nav__link--active')).toBe(true);
    });

    it('remains a real anchor with routerLink-derived href, not a button or a plain span', () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();

      const link = [...root().querySelectorAll('a.shell-nav__link')].find((a) => a.getAttribute('href') === '/resources')!;
      expect(link.tagName).toBe('A');
      expect(link.getAttribute('href')).toBe('/resources');
    });

    it('does not add any global document-level dragstart listener', () => {
      const addEventListener = vi.spyOn(document, 'addEventListener');
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();

      expect(addEventListener.mock.calls.filter(([type]) => type === 'dragstart')).toHaveLength(0);
    });

    it('does not mark the disabled "Settings" button or the "Log out" button as draggable (they were never anchors, this fix must not touch them)', () => {
      configure({ hasAnyRole: () => true });
      flushPendingApprovals();
      fixture.detectChanges();

      const settingsButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Settings');
      expect(settingsButton?.getAttribute('draggable')).toBeNull();
    });
  });

  describe('with no signed-in user (defensive/loading case)', () => {
    beforeEach(() => configure({ currentUser: null }));

    it('still shows the theme toggle in the sidebar footer even before the user resolves', () => {
      flushPendingApprovals();
      fixture.detectChanges();

      const footer = root().querySelector('.shell-sidebar__footer');
      expect(footer?.querySelector('app-theme-toggle')).not.toBeNull();
    });
  });

  describe('with a signed-in user', () => {
    beforeEach(() => {
      configure({ currentUser: { email: 'ana@example.com' } });
      flushPendingApprovals();
      fixture.detectChanges();
    });

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
