import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { DIAGNOSTICS_ENABLED, DiagnosticsService } from './diagnostics.service';

// Only the diagnostic service itself is tested here, per the scope this instrumentation was built under -
// it is temporary, development-only tooling for an unidentified bug, not application behavior, so it gets
// coverage for its own mechanics (buffer bound, sanitization, listener lifecycle, prod gating) and nothing
// speculative about the freeze investigation itself.
//
// Every test builds its own isolated child EnvironmentInjector (same pattern AuthService's own spec uses
// for its "isAuthenticated freshness" tests) and constructs DiagnosticsService directly within it, rather
// than relying on TestBed.inject()'s root-singleton caching across `it()` blocks. This service registers
// real, ambient side effects on the shared `document`/`window` objects (a capture-phase click listener,
// a global object) the moment it's constructed - giving each test its own injector, explicitly destroyed
// in afterEach, is what keeps one test's listener from still being live during a later test's assertions.
describe('DiagnosticsService', () => {
  let injector: EnvironmentInjector | undefined;

  function createService(enabled = true): DiagnosticsService {
    TestBed.configureTestingModule({ providers: [provideRouter([{ path: 'login', children: [] }])] });
    injector = createEnvironmentInjector([{ provide: DIAGNOSTICS_ENABLED, useValue: enabled }], TestBed.inject(EnvironmentInjector));
    return runInInjectionContext(injector, () => new DiagnosticsService());
  }

  afterEach(() => {
    injector?.destroy();
    injector = undefined;
    delete window.__bookSpaceDiagnostics;
  });

  it('bounds the in-memory event buffer to the latest 100 events', () => {
    const service = createService();

    for (let i = 0; i < 150; i++) {
      document.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: 10, clientY: 10 }));
    }

    const events = service.snapshot().events;
    expect(events).toHaveLength(100);
    // The oldest 50 of 150 clicks must have been evicted - only seq 51..150 remain, oldest-first.
    expect(events[0]).toMatchObject({ type: 'click', seq: 51 });
    expect(events[99]).toMatchObject({ type: 'click', seq: 150 });
  });

  it('never records header, body, or other non-listed fields on an HTTP event', () => {
    const service = createService();

    const id = service.recordHttpStart('POST', 'http://localhost:5185/auth/refresh');
    service.recordHttpOutcome(id, 'http-success', 200, 42);

    const events = service.snapshot().events;
    const started = events.find((event) => event.type === 'http-start');
    const succeeded = events.find((event) => event.type === 'http-success');

    // Exhaustive allow-lists, not deny-lists: any field this service didn't explicitly design in - most
    // importantly headers/body/authorization - fails this immediately, not just the ones named. http-start
    // legitimately has fewer fields than http-success (no status/duration yet - the request just began).
    expect(Object.keys(started!).sort()).toEqual(['method', 'path', 'requestId', 'timestamp', 'type'].sort());
    expect(Object.keys(succeeded!).sort()).toEqual(['durationMs', 'method', 'path', 'requestId', 'status', 'timestamp', 'type'].sort());
  });

  it('strips query parameters from every recorded HTTP path, so a token-bearing query string can never be recorded', () => {
    const service = createService();

    const id = service.recordHttpStart('GET', 'http://localhost:5185/bookings?accessToken=super-secret-value&page=2');
    service.recordHttpOutcome(id, 'http-success', 200, 5);

    const events = service.snapshot().events;
    const started = events.find((event) => event.type === 'http-start');
    const succeeded = events.find((event) => event.type === 'http-success');

    expect(started).toMatchObject({ path: '/bookings' });
    expect(succeeded).toMatchObject({ path: '/bookings' });
    const serialized = JSON.stringify(events);
    expect(serialized).not.toContain('super-secret-value');
    expect(serialized).not.toContain('?');
  });

  it('registers exactly one document click listener for a single service instance', () => {
    const addEventListener = vi.spyOn(document, 'addEventListener');

    createService();

    expect(addEventListener.mock.calls.filter(([type]) => type === 'click')).toHaveLength(1);
  });

  it('removes its click listener when its owning injector is destroyed', () => {
    createService();
    const removeEventListener = vi.spyOn(document, 'removeEventListener');

    injector!.destroy();
    injector = undefined; // already destroyed above - afterEach must not destroy it a second time

    expect(removeEventListener.mock.calls.filter(([type]) => type === 'click')).toHaveLength(1);
  });

  it('exposes window.__bookSpaceDiagnostics when enabled', () => {
    createService(true);

    expect(typeof window.__bookSpaceDiagnostics?.snapshot).toBe('function');
    expect(typeof window.__bookSpaceDiagnostics?.copy).toBe('function');
  });

  // Also covers "no click listener is registered when disabled": registerClickListener() and
  // exposeGlobal() are both called unconditionally, one after the other, from the same initialize()
  // that only ever runs when enabled - proving window.__bookSpaceDiagnostics is absent proves
  // initialize() didn't run at all, which proves the click listener wasn't registered either.
  it('never exposes window.__bookSpaceDiagnostics when disabled (production)', () => {
    createService(false);

    expect(window.__bookSpaceDiagnostics).toBeUndefined();
  });

  it('exposes sidebarSnapshot() and routerSnapshot() alongside snapshot()/copy() when enabled', () => {
    createService(true);

    expect(typeof window.__bookSpaceDiagnostics?.sidebarSnapshot).toBe('function');
    expect(typeof window.__bookSpaceDiagnostics?.routerSnapshot).toBe('function');
  });

  // Directly answers one of the "why are click diagnostics missing" questions the freeze investigation
  // raised: does a real click on a real sidebar link get recorded at all? Yes - this dispatches an actual
  // MouseEvent at a real <a class="shell-nav__link"> element and checks the click buffer, not a synthetic
  // call into private methods.
  describe('recording real clicks, including edge-case targets', () => {
    let root: HTMLElement;

    beforeEach(() => {
      root = document.createElement('div');
      root.innerHTML = `
        <div class="shell">
          <aside class="shell-sidebar">
            <nav class="shell-nav">
              <a class="shell-nav__link" href="/resources">
                <svg class="icon" viewBox="0 0 10 10"><path d="M0 0 L10 10" /></svg>
                <span>Resources</span>
              </a>
            </nav>
          </aside>
        </div>
      `;
      document.body.appendChild(root);
    });

    afterEach(() => {
      root.remove();
    });

    function dispatchClickOn(el: Element): void {
      el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: 5, clientY: 5 }));
    }

    it('records a first sidebar click', () => {
      const service = createService();
      const link = root.querySelector('a.shell-nav__link')!;

      dispatchClickOn(link);

      const clicks = service.snapshot().events.filter((e) => e.type === 'click');
      expect(clicks).toHaveLength(1);
      expect(clicks[0]).toMatchObject({ seq: 1, targetTag: 'A' });
    });

    it('records a second, immediately-following rapid click', () => {
      const service = createService();
      const link = root.querySelector('a.shell-nav__link')!;

      dispatchClickOn(link);
      dispatchClickOn(link);

      const clicks = service.snapshot().events.filter((e) => e.type === 'click');
      expect(clicks.map((c) => (c.type === 'click' ? c.seq : null))).toEqual([1, 2]);
      expect(service.clickCount()).toBe(2);
    });

    it('records a click whose target is a nested SVG element without throwing (className is an SVGAnimatedString, not a string)', () => {
      const service = createService();
      const svgPath = root.querySelector('.icon path')!;

      expect(() => dispatchClickOn(svgPath)).not.toThrow();

      const clicks = service.snapshot().events.filter((e) => e.type === 'click');
      expect(clicks).toHaveLength(1);
      expect(clicks[0]).toMatchObject({ targetTag: 'path' });
      // No self-reported diagnostic-error event either - this must be a clean recording, not a caught throw.
      expect(service.snapshot().events.some((e) => e.type === 'diagnostic-error')).toBe(false);
    });

    it('records a click whose target is a nested <span> element cleanly', () => {
      const service = createService();
      const span = root.querySelector('a.shell-nav__link span')!;

      dispatchClickOn(span);

      const clicks = service.snapshot().events.filter((e) => e.type === 'click');
      expect(clicks).toHaveLength(1);
      expect(clicks[0]).toMatchObject({ targetTag: 'SPAN' });
    });

    // Proves the try/catch actually recovers rather than permanently breaking the listener: forces
    // recordClick's own logic to throw on the FIRST call (via a temporarily-throwing
    // document.elementsFromPoint), then restores it and confirms the SECOND click is still recorded
    // normally - a native addEventListener callback that throws once does not unregister itself, and
    // this is that guarantee exercised for real rather than just asserted.
    it('keeps recording subsequent clicks even after the click handler throws once', () => {
      const service = createService();
      const link = root.querySelector('a.shell-nav__link')!;
      const original = document.elementsFromPoint;
      (document as unknown as { elementsFromPoint: () => Element[] }).elementsFromPoint = () => {
        throw new Error('simulated failure inside the click handler');
      };

      dispatchClickOn(link);

      (document as unknown as { elementsFromPoint: typeof original }).elementsFromPoint = original;
      dispatchClickOn(link);

      const events = service.snapshot().events;
      expect(events.filter((e) => e.type === 'diagnostic-error')).toHaveLength(1);
      const clicks = events.filter((e) => e.type === 'click');
      expect(clicks).toHaveLength(1);
      // seq is 1, not 2: the throwing computation runs BEFORE clickSeq advances (see recordClick), so
      // the failed first attempt never consumes a sequence number - seq counts successful recordings.
      expect(clicks[0]).toMatchObject({ seq: 1 });
    });
  });

  // Diagnostic code must never alter real application interaction - proves the click listener never
  // calls preventDefault/stopPropagation/stopImmediatePropagation, by spying on the real MouseEvent
  // methods and asserting they're never invoked.
  it('never calls preventDefault/stopPropagation/stopImmediatePropagation from the diagnostic click listener', () => {
    createService();
    const link = document.createElement('a');
    link.className = 'shell-nav__link';
    link.setAttribute('href', '/resources');
    document.body.appendChild(link);

    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    const preventDefault = vi.spyOn(event, 'preventDefault');
    const stopPropagation = vi.spyOn(event, 'stopPropagation');
    const stopImmediatePropagation = vi.spyOn(event, 'stopImmediatePropagation');

    link.dispatchEvent(event);

    expect(preventDefault).not.toHaveBeenCalled();
    expect(stopPropagation).not.toHaveBeenCalled();
    expect(stopImmediatePropagation).not.toHaveBeenCalled();
    link.remove();
  });

  describe('sidebarSnapshot()', () => {
    let container: HTMLElement;

    beforeEach(() => {
      container = document.createElement('div');
      container.innerHTML = `
        <app-shell>
          <div class="shell">
            <aside class="shell-sidebar">
              <nav class="shell-nav">
                <a class="shell-nav__link" href="/">Dashboard</a>
                <a class="shell-nav__link" href="/resources">Resources</a>
              </nav>
            </aside>
            <main class="shell-content"></main>
          </div>
        </app-shell>
      `;
      document.body.appendChild(container);
    });

    afterEach(() => {
      container.remove();
      delete (document as unknown as { elementFromPoint?: unknown }).elementFromPoint;
      delete (document as unknown as { elementsFromPoint?: unknown }).elementsFromPoint;
    });

    it('reports one entry per sidebar link, with hit-testing marked healthy when the point resolves back to the link itself', () => {
      const service = createService();
      const dashboardLink = container.querySelector('a[href="/"]')!;
      (document as unknown as { elementFromPoint: () => Element }).elementFromPoint = () => dashboardLink;
      (document as unknown as { elementsFromPoint: () => Element[] }).elementsFromPoint = () => [dashboardLink];

      const snapshot = service.sidebarSnapshot();

      expect(snapshot.links).toHaveLength(2);
      expect(snapshot.links.map((l) => l.href)).toEqual(['/', '/resources']);
      expect(snapshot.links[0].topElementIsLinkOrDescendant).toBe(true);
      expect(snapshot.sidebarCount).toBe(1);
      expect(snapshot.navContainerCount).toBe(1);
      expect(snapshot.linkCountByHref).toEqual({ '/': 1, '/resources': 1 });
    });

    // This is the exact scenario the task's interpretation table calls a hit-testing failure: something
    // OTHER than the link (or a descendant of it) is what actually receives a click at the link's own
    // center point. sidebarSnapshot() must surface that plainly rather than assuming the link is fine.
    it('marks hit-testing as unhealthy when elementFromPoint resolves to something other than the link', () => {
      const service = createService();
      const impostor = document.createElement('div');
      document.body.appendChild(impostor);
      (document as unknown as { elementFromPoint: () => Element }).elementFromPoint = () => impostor;
      (document as unknown as { elementsFromPoint: () => Element[] }).elementsFromPoint = () => [impostor];

      const snapshot = service.sidebarSnapshot();

      expect(snapshot.links[0].topElementIsLinkOrDescendant).toBe(false);
      expect(snapshot.links[0].elementAtCenter).toMatchObject({ tag: 'DIV' });
      impostor.remove();
    });

    it('flags an ancestor with pointer-events: none as a concern', () => {
      const service = createService();
      (container.querySelector('.shell-sidebar') as HTMLElement).style.pointerEvents = 'none';
      (document as unknown as { elementFromPoint: () => Element | null }).elementFromPoint = () => null;
      (document as unknown as { elementsFromPoint: () => Element[] }).elementsFromPoint = () => [];

      const snapshot = service.sidebarSnapshot();

      expect(snapshot.links[0].ancestorConcerns.some((c) => c.includes('pointer-events: none'))).toBe(true);
    });

    it('reports zero shells/sidebars when none are rendered, rather than throwing', () => {
      container.remove();
      const service = createService();

      const snapshot = service.sidebarSnapshot();

      expect(snapshot.links).toEqual([]);
      expect(snapshot.appShellCount).toBe(0);
      expect(snapshot.sidebarCount).toBe(0);
      container = document.createElement('div'); // afterEach expects `container` to still be removable
      document.body.appendChild(container);
    });
  });
});
