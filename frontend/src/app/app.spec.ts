import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });
});

@Component({ selector: 'app-empty-stand-in', template: '' })
class EmptyStandInComponent {}

// Unit-level edge-case coverage for App.onInternalLinkDragStart (see app.ts's own comment on it) - the
// centralized fix for the native browser link-drag freeze (pointerdown -> mousedown -> dragstart ->
// pointercancel) originally found and fixed narrowly on the sidebar, then reproduced on the Users list
// row. Built against synthetic anchors appended directly to the real app-root host element (dragstart
// bubbles up to the @HostListener exactly the same way it would from a real page), so every edge case can
// be exercised precisely and cheaply without standing up a full routed page for each one.
//
// See internal-link-native-drag.spec.ts for the complementary integration coverage proving this SAME
// mechanism reaches real, fully-rendered pages - the actual Users list row, the Resource detail page, and
// the sidebar - closing the loop with where the original bug was actually found.
describe('App - onInternalLinkDragStart (centralized internal-link native-drag prevention)', () => {
  let fixture: ComponentFixture<App>;
  let host: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    });
    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  function appendAnchor(href: string | null, className = ''): HTMLAnchorElement {
    const anchor = document.createElement('a');
    if (href !== null) {
      anchor.setAttribute('href', href);
    }
    if (className) {
      anchor.className = className;
    }
    host.appendChild(anchor);
    return anchor;
  }

  function dragstartOn(el: Element | Text): Event {
    const event = new Event('dragstart', { bubbles: true, cancelable: true });
    el.dispatchEvent(event);
    return event;
  }

  it('prevents dragstart on an internal same-origin anchor (root-relative href, exactly how every routerLink renders)', () => {
    const anchor = appendAnchor('/resources');
    expect(dragstartOn(anchor).defaultPrevented).toBe(true);
  });

  it('prevents dragstart when the event target is a child element inside the anchor', () => {
    const anchor = appendAnchor('/users/42');
    const label = document.createElement('span');
    anchor.appendChild(label);

    expect(dragstartOn(label).defaultPrevented).toBe(true);
  });

  it('prevents dragstart on an anchor shaped like the real Users list row (.user-row, /users/:id)', () => {
    const anchor = appendAnchor('/users/7', 'user-row');
    expect(dragstartOn(anchor).defaultPrevented).toBe(true);
  });

  it('prevents dragstart on an anchor shaped like a real Resource detail link (/resources/:id)', () => {
    const anchor = appendAnchor('/resources/3');
    expect(dragstartOn(anchor).defaultPrevented).toBe(true);
  });

  it('prevents dragstart on an anchor shaped like a real sidebar nav link (.shell-nav__link)', () => {
    const anchor = appendAnchor('/calendar', 'shell-nav__link');
    expect(dragstartOn(anchor).defaultPrevented).toBe(true);
  });

  it('does not prevent dragstart on an external https link', () => {
    const anchor = appendAnchor('https://example.com/some-page');
    expect(dragstartOn(anchor).defaultPrevented).toBe(false);
  });

  it('does not prevent dragstart on a mailto: link', () => {
    const anchor = appendAnchor('mailto:someone@example.com');
    expect(dragstartOn(anchor).defaultPrevented).toBe(false);
  });

  it('does not prevent dragstart on a tel: link', () => {
    const anchor = appendAnchor('tel:+15551234567');
    expect(dragstartOn(anchor).defaultPrevented).toBe(false);
  });

  it('does not prevent dragstart on a non-anchor draggable element', () => {
    const div = document.createElement('div');
    div.setAttribute('draggable', 'true');
    host.appendChild(div);

    expect(dragstartOn(div).defaultPrevented).toBe(false);
  });

  it('does not throw, and does not prevent dragstart, when the anchor has no href', () => {
    const anchor = appendAnchor(null);
    expect(() => dragstartOn(anchor)).not.toThrow();
    expect(dragstartOn(anchor).defaultPrevented).toBe(false);
  });

  it('does not throw when the anchor href cannot be parsed as a URL', () => {
    const anchor = appendAnchor('/resources'); // a real href, so it passes the hasAttribute('href') guard
    // Forces `new URL(anchor.href)` inside the handler to throw, exercising its try/catch directly - a
    // real browser's own href-reflection algorithm can return the raw, unparsed attribute value for a
    // malformed href (see app.ts's own comment on anchor.href), which this simulates directly rather than
    // depending on a specific jsdom URL-parsing quirk to reproduce the same failure.
    Object.defineProperty(anchor, 'href', { value: 'this is not a valid url' });

    const event = dragstartOn(anchor);
    expect(event.defaultPrevented).toBe(false);
  });

  it('does not throw when the dragstart target is a text node', () => {
    const anchor = appendAnchor('/resources');
    const text = document.createTextNode('Resources');
    anchor.appendChild(text);

    expect(() => dragstartOn(text)).not.toThrow();
  });

  it('does not throw when window is unavailable (e.g. a non-browser/SSR-like environment)', () => {
    const anchor = appendAnchor('/resources');
    // Calling the handler directly, rather than dispatching a real DOM event, is deliberate here: actually
    // removing `window` from a live jsdom + zone.js test environment (via a real dragstart dispatch) would
    // risk destabilizing unrelated test machinery that itself depends on `window` existing. This isolates
    // exactly the `typeof window === 'undefined'` guard the handler uses.
    const app = fixture.componentInstance as unknown as { onInternalLinkDragStart(event: DragEvent): void };

    vi.stubGlobal('window', undefined);
    try {
      expect(() => app.onInternalLinkDragStart({ target: anchor } as unknown as DragEvent)).not.toThrow();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('only ever prevents dragstart - pointerdown, mousedown, pointerup, mouseup, and click on an internal link are left alone', () => {
    const anchor = appendAnchor('/resources');
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
      const event = new Event(type, { bubbles: true, cancelable: true });
      anchor.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
    }
  });

  it('does not intercept a Ctrl/Cmd+click (open-in-new-tab click) on an internal link', () => {
    const anchor = appendAnchor('/resources');

    const ctrlClick = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true });
    anchor.dispatchEvent(ctrlClick);
    expect(ctrlClick.defaultPrevented).toBe(false);

    const metaClick = new MouseEvent('click', { bubbles: true, cancelable: true, metaKey: true });
    anchor.dispatchEvent(metaClick);
    expect(metaClick.defaultPrevented).toBe(false);
  });

  it('leaves keyboard focus on an internal link working, unaffected by the drag handler', () => {
    const anchor = appendAnchor('/resources');
    anchor.tabIndex = 0;

    anchor.focus();

    expect(document.activeElement).toBe(anchor);
  });
});

describe('App - single dragstart handler for the app lifetime', () => {
  it('invokes the handler exactly once per dragstart, even after repeated route navigation (one App instance, HostListener lifecycle)', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([
          { path: 'a', component: EmptyStandInComponent },
          { path: 'b', component: EmptyStandInComponent },
        ]),
      ],
    });

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/a');
    fixture.detectChanges();
    await router.navigateByUrl('/b');
    fixture.detectChanges();
    await router.navigateByUrl('/a');
    fixture.detectChanges();

    const app = fixture.componentInstance as unknown as Record<string, (event: Event) => void>;
    const spy = vi.spyOn(app, 'onInternalLinkDragStart');

    const anchor = document.createElement('a');
    anchor.setAttribute('href', '/resources');
    (fixture.nativeElement as HTMLElement).appendChild(anchor);
    anchor.dispatchEvent(new Event('dragstart', { bubbles: true, cancelable: true }));

    expect(spy).toHaveBeenCalledTimes(1);
  });
});
