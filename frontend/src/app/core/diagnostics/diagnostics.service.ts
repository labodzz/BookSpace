import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  GuardsCheckEnd,
  GuardsCheckStart,
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationStart,
  ResolveEnd,
  ResolveStart,
  RouteConfigLoadEnd,
  RouteConfigLoadStart,
  Router,
} from '@angular/router';
import { filter } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  ActiveHttpRequestSnapshot,
  AncestorStyleSnapshot,
  AuthRefreshPhase,
  ContainerSnapshot,
  DiagnosticEvent,
  DiagnosticsSnapshot,
  ElementSnapshot,
  RouterDiagnosticEvent,
  RouterEventKind,
  RouterSnapshot,
  SidebarLinkSnapshot,
  SidebarSnapshot,
} from './diagnostics.models';

const SIDEBAR_LINK_SELECTOR = '.shell-nav__link';

const MAX_ROUTER_EVENTS = 50;

// Development-only diagnostic recorder, originally built to investigate the "page freezes after rapid
// sidebar clicks" report. That investigation is resolved: the root cause was the browser's native
// link-drag behavior on the sidebar anchors (fixed via draggable="false" - see shell.html/shell.scss),
// not anything this service's own event/router/HTTP/auth-refresh tracking covers. Kept afterward as a
// general-purpose, safe development facility (routerSnapshot()/sidebarSnapshot()/snapshot() are broadly
// useful for any future navigation/rendering issue), not because the original bug is still open. None of
// it runs in production (gated by DIAGNOSTICS_ENABLED, which defaults to `!environment.production`).
//
// Hard privacy rule for every event/snapshot this service ever produces: NEVER record access tokens,
// refresh tokens, invitation tokens, passwords, Authorization headers, request/response bodies, or any
// other personal data. Only safe technical metadata (URL paths with no query string, tag names, class
// names, timestamps, counts, durations, status codes, computed-style values) - see diagnostics.models.ts
// for the exhaustive list of fields every event type is allowed to carry. Nothing is ever sent to the
// backend and nothing is ever written to localStorage/sessionStorage - the buffer lives in memory only
// and is gone the moment the page is reloaded or this service is destroyed.
const MAX_EVENTS = 100;

// A plain `environment.production` read inside the service would be untestable the normal way -
// Angular's test builder disallows vi.mock-ing a relative import like `environment` (see isApiRequest's
// own comment on this exact constraint in auth.interceptor.ts). Routing it through an overridable
// injection token instead lets diagnostics.service.spec.ts simulate "production" via a TestBed provider
// override, without needing to touch the environment module at all. Every real (non-test) injector gets
// this same default.
export const DIAGNOSTICS_ENABLED = new InjectionToken<boolean>('DIAGNOSTICS_ENABLED', {
  providedIn: 'root',
  factory: () => !environment.production,
});

declare global {
  interface Window {
    __bookSpaceDiagnostics?: {
      snapshot(): DiagnosticsSnapshot;
      copy(): Promise<void>;
      routerSnapshot(): RouterSnapshot;
      sidebarSnapshot(): SidebarSnapshot;
    };
  }
}

// NavigationError.error is typed `any` - a failed lazy chunk import rejects with a real Error (message
// only ever contains the chunk's own module specifier/URL, never anything user-supplied), but nothing
// guarantees a THIRD-PARTY guard/resolver couldn't reject with something else entirely. Reducing to a
// message string (or a fixed placeholder) is what keeps this side of the "never record...personal data"
// line no matter what shape the underlying error actually has.
function sanitizeRouterError(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }
  if (typeof error === 'string') {
    return error;
  }
  return '(non-Error value thrown)';
}

// .toast-stack is deliberately NOT in this one: app.html renders <app-toast>'s container
// unconditionally (see toast.html), so it exists in the DOM at all times whether or not any toast is
// currently showing - including it here would make hasBlockingOverlay() true on literally every click,
// always, regardless of whether anything is actually there. It's still listed separately in
// SNAPSHOT_ELEMENT_SELECTOR below, where its full computed style (pointer-events: none when empty) makes
// its harmlessness self-evident instead of collapsing it into a blunt yes/no flag.
const BLOCKING_OVERLAY_SELECTOR = '.dialog-backdrop, .dialog, [role="dialog"], [class*="overlay"]';
const SNAPSHOT_ELEMENT_SELECTOR = '.dialog-backdrop, .dialog, [role="dialog"], .toast-stack, [class*="overlay"]';

function hasBlockingOverlay(): boolean {
  if (document.querySelector(BLOCKING_OVERLAY_SELECTOR)) {
    return true;
  }
  const toastStack = document.querySelector('.toast-stack');
  return !!toastStack && toastStack.children.length > 0;
}

function pathOnly(url: string): string {
  try {
    const origin = typeof location !== 'undefined' ? location.origin : 'http://localhost';
    return new URL(url, origin).pathname;
  } catch {
    return url.split('?')[0];
  }
}

function describeElement(el: Element | null): { tag: string; classes: string } {
  if (!el) {
    return { tag: '(none)', classes: '' };
  }
  const classes =
    typeof el.className === 'string'
      ? el.className
      : ((el as unknown as { className?: { baseVal?: string } }).className?.baseVal ?? '');
  return { tag: el.tagName, classes };
}

function describeElementStyle(el: Element): ElementSnapshot {
  const { tag, classes } = describeElement(el);
  const style = getComputedStyle(el);
  return {
    tag,
    classes,
    display: style.display,
    visibility: style.visibility,
    opacity: style.opacity,
    pointerEvents: style.pointerEvents,
    position: style.position,
    zIndex: style.zIndex,
  };
}

// "top N elements at a point" as short, readable strings (TAG.class.class) - shared by click recording
// and sidebarSnapshot() so both report hit-testing the same way.
function topElementStrings(x: number, y: number, limit = 5): string[] {
  if (typeof document.elementsFromPoint !== 'function') {
    return [];
  }
  return document
    .elementsFromPoint(x, y)
    .slice(0, limit)
    .map((el) => {
      const described = describeElement(el);
      return described.classes ? `${described.tag}.${described.classes.trim().split(/\s+/).join('.')}` : described.tag;
    });
}

// Walks from `el` up to (and including) the first ancestor matching `stopSelector`, recording exactly
// the style facts the task asks about for each one. Stops after including the stop element itself so the
// chain is always bounded (never walks past <app-shell> to <body>/<html>).
function describeAncestors(el: Element, stopSelector: string): AncestorStyleSnapshot[] {
  const chain: AncestorStyleSnapshot[] = [];
  let current: Element | null = el.parentElement;
  let guard = 0;
  while (current && guard < 50) {
    guard += 1;
    const { tag, classes } = describeElement(current);
    const style = getComputedStyle(current);
    chain.push({
      tag,
      classes,
      pointerEvents: style.pointerEvents,
      visibility: style.visibility,
      display: style.display,
      position: style.position,
      zIndex: style.zIndex,
      transform: style.transform,
      overflow: style.overflow,
      inert: (current as HTMLElement).inert === true,
    });
    if (current.matches(stopSelector)) {
      break;
    }
    current = current.parentElement;
  }
  return chain;
}

function ancestorConcerns(ancestors: AncestorStyleSnapshot[]): string[] {
  const concerns: string[] = [];
  for (const ancestor of ancestors) {
    const label = ancestor.classes ? `${ancestor.tag}.${ancestor.classes.trim().split(/\s+/).join('.')}` : ancestor.tag;
    if (ancestor.pointerEvents === 'none') {
      concerns.push(`${label}: pointer-events: none`);
    }
    if (ancestor.visibility === 'hidden' || ancestor.visibility === 'collapse') {
      concerns.push(`${label}: visibility: ${ancestor.visibility}`);
    }
    if (ancestor.display === 'none') {
      concerns.push(`${label}: display: none`);
    }
    // Empty string is jsdom's getComputedStyle() answer for "no rule set this property" (real browsers
    // resolve it to the CSS-spec initial value instead, 'visible'/'none' respectively) - treated as safe
    // here explicitly, rather than via an exclusion list, so an unrecognized-but-harmless value from any
    // environment doesn't produce a false "concern". 'hidden' is also safe: unlike pointer-events/
    // visibility/display above, whether an ancestor's overflow: hidden actually clips a descendant is a
    // pure layout-geometry question (is the descendant positioned within the ancestor's visible bounds?)
    // that this static CSS-declaration check cannot answer - see the getBoundingClientRect() disclaimer
    // on the caller below. The sidebar's own fixed-height, independently-scrolling layout (.shell-sidebar/
    // :host) relies on overflow: hidden precisely so the nav links stay fully visible and scrollable
    // within it, never to hide them.
    const SAFE_OVERFLOW = new Set(['visible', 'auto', 'unset', 'hidden', '']);
    if (!SAFE_OVERFLOW.has(ancestor.overflow)) {
      concerns.push(`${label}: overflow: ${ancestor.overflow}`);
    }
    if (ancestor.transform !== 'none' && ancestor.transform !== '') {
      concerns.push(`${label}: transform: ${ancestor.transform} (new stacking/containing-block context)`);
    }
    if (ancestor.inert) {
      concerns.push(`${label}: inert`);
    }
  }
  return concerns;
}

function describeContainer(selector: string): ContainerSnapshot {
  const el = document.querySelector(selector);
  if (!el) {
    return { found: false, tag: '(none)', classes: '', rect: null, pointerEvents: null, position: null, zIndex: null, overflow: null, transform: null };
  }
  const { tag, classes } = describeElement(el);
  const style = getComputedStyle(el);
  const rect = el.getBoundingClientRect();
  return {
    found: true,
    tag,
    classes,
    rect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
    pointerEvents: style.pointerEvents,
    position: style.position,
    zIndex: style.zIndex,
    overflow: style.overflow,
    transform: style.transform,
  };
}

interface TrackedHttpRequest {
  method: string;
  path: string;
  startedAt: number;
  settled: boolean;
}

@Injectable({ providedIn: 'root' })
export class DiagnosticsService {
  private readonly enabled = inject(DIAGNOSTICS_ENABLED);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly buffer: DiagnosticEvent[] = [];
  private readonly routerEventBuffer: RouterDiagnosticEvent[] = [];
  private readonly activeHttp = new Map<number, TrackedHttpRequest>();
  private initialized = false;
  private clickSeq = 0;
  private httpSeq = 0;

  private lastNavigationStart: RouterDiagnosticEvent | null = null;
  private lastNavigationEnd: RouterDiagnosticEvent | null = null;
  private lastNavigationCancel: RouterDiagnosticEvent | null = null;
  private lastNavigationError: RouterDiagnosticEvent | null = null;

  readonly clickCount = signal(0);
  readonly httpTotalCount = signal(0);
  readonly navigationState = signal<'idle' | 'running'>('idle');
  readonly authRefreshState = signal<AuthRefreshPhase | 'idle'>('idle');

  constructor() {
    if (this.enabled) {
      this.initialize();
    }
  }

  // --- one-time setup -------------------------------------------------------------------------------

  private initialize(): void {
    if (this.initialized) {
      return;
    }
    this.initialized = true;

    this.registerClickListener();
    this.registerNavigationListener();
    this.registerLongTaskObserver();
    this.exposeGlobal();
  }

  private registerClickListener(): void {
    const handler = (event: MouseEvent) => this.handleClick(event);
    // Capture phase, so this sees the click even if a component's own handler calls
    // stopPropagation() on the bubble phase (e.g. cancel-booking-dialog's inner .dialog click guard).
    document.addEventListener('click', handler, true);
    this.destroyRef.onDestroy(() => document.removeEventListener('click', handler, true));
  }

  private registerNavigationListener(): void {
    type TrackedRouterEvent =
      | NavigationStart
      | RouteConfigLoadStart
      | RouteConfigLoadEnd
      | GuardsCheckStart
      | GuardsCheckEnd
      | ResolveStart
      | ResolveEnd
      | NavigationCancel
      | NavigationError
      | NavigationEnd;

    this.router.events
      .pipe(
        filter(
          (event): event is TrackedRouterEvent =>
            event instanceof NavigationStart ||
            event instanceof RouteConfigLoadStart ||
            event instanceof RouteConfigLoadEnd ||
            event instanceof GuardsCheckStart ||
            event instanceof GuardsCheckEnd ||
            event instanceof ResolveStart ||
            event instanceof ResolveEnd ||
            event instanceof NavigationCancel ||
            event instanceof NavigationError ||
            event instanceof NavigationEnd,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => this.recordRouterEvent(event));
  }

  private recordRouterEvent(
    event:
      | NavigationStart
      | RouteConfigLoadStart
      | RouteConfigLoadEnd
      | GuardsCheckStart
      | GuardsCheckEnd
      | ResolveStart
      | ResolveEnd
      | NavigationCancel
      | NavigationError
      | NavigationEnd,
  ): void {
    // RouteConfigLoadStart/End (bracketing a lazy loadComponent() import()) are the only two of these
    // that carry no `.id`/`.url` at all - see diagnostics.models.ts's RouterDiagnosticEvent comment.
    if (event instanceof RouteConfigLoadStart || event instanceof RouteConfigLoadEnd) {
      const kind: RouterEventKind = event instanceof RouteConfigLoadStart ? 'route-config-load-start' : 'route-config-load-end';
      this.pushRouterEvent({ type: kind, timestamp: Date.now(), navigationId: null, path: event.route.path ?? '' });
      return;
    }

    const kind: RouterEventKind =
      event instanceof NavigationStart
        ? 'nav-start'
        : event instanceof GuardsCheckStart
          ? 'guards-check-start'
          : event instanceof GuardsCheckEnd
            ? 'guards-check-end'
            : event instanceof ResolveStart
              ? 'resolve-start'
              : event instanceof ResolveEnd
                ? 'resolve-end'
                : event instanceof NavigationCancel
                  ? 'nav-cancel'
                  : event instanceof NavigationError
                    ? 'nav-error'
                    : 'nav-end';

    if (kind === 'nav-start') {
      this.navigationState.set('running');
    } else if (kind === 'nav-end' || kind === 'nav-cancel' || kind === 'nav-error') {
      this.navigationState.set('idle');
    }

    const diagnosticEvent: RouterDiagnosticEvent = {
      type: kind,
      timestamp: Date.now(),
      navigationId: event.id,
      path: pathOnly(event.url),
      ...(event instanceof NavigationCancel
        ? { cancellationCode: event.code !== undefined ? String(event.code) : undefined, reason: event.reason }
        : {}),
      ...(event instanceof NavigationError ? { error: sanitizeRouterError(event.error) } : {}),
    };

    this.pushRouterEvent(diagnosticEvent);
    if (kind === 'nav-start') {
      this.lastNavigationStart = diagnosticEvent;
    } else if (kind === 'nav-end') {
      this.lastNavigationEnd = diagnosticEvent;
    } else if (kind === 'nav-cancel') {
      this.lastNavigationCancel = diagnosticEvent;
    } else if (kind === 'nav-error') {
      this.lastNavigationError = diagnosticEvent;
    }
  }

  private pushRouterEvent(event: RouterDiagnosticEvent): void {
    this.push(event);
    this.routerEventBuffer.push(event);
    if (this.routerEventBuffer.length > MAX_ROUTER_EVENTS) {
      this.routerEventBuffer.shift();
    }
  }

  private registerLongTaskObserver(): void {
    if (typeof PerformanceObserver === 'undefined') {
      return;
    }
    try {
      if (PerformanceObserver.supportedEntryTypes && !PerformanceObserver.supportedEntryTypes.includes('longtask')) {
        return;
      }
      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          this.push({ type: 'long-task', timestamp: Date.now(), startTime: entry.startTime, duration: entry.duration });
        }
      });
      observer.observe({ entryTypes: ['longtask'] });
      this.destroyRef.onDestroy(() => observer.disconnect());
    } catch {
      // Long-task observation is purely best-effort diagnostics - a browser that throws here is treated
      // the same as one that simply doesn't support it: fail silently, record nothing.
    }
  }

  private exposeGlobal(): void {
    if (typeof window === 'undefined') {
      return;
    }
    window.__bookSpaceDiagnostics = {
      snapshot: () => this.snapshot(),
      copy: () => this.copy(),
      routerSnapshot: () => this.routerSnapshot(),
      sidebarSnapshot: () => this.sidebarSnapshot(),
    };
    this.destroyRef.onDestroy(() => delete window.__bookSpaceDiagnostics);
  }

  // --- click recording --------------------------------------------------------------------------------

  // Wrapped in try/catch deliberately: a native addEventListener callback that throws does NOT
  // unregister itself - the browser logs the error and calls it again normally on the next event - but
  // an uncaught throw here would still be reported as an unhandled exception in the console for
  // something that is supposed to be invisible diagnostic plumbing, and (see recordDiagnosticError) would
  // otherwise leave no trace in the buffer of why a click went unrecorded. If this ever fires, it proves
  // the listener recovers on its own; it does not mean clicks stopped being tracked.
  private handleClick(event: MouseEvent): void {
    try {
      this.recordClick(event);
    } catch (error) {
      this.recordDiagnosticError('click', error);
    }
  }

  private recordClick(event: MouseEvent): void {
    const target = event.target instanceof Element ? event.target : null;
    const { tag, classes } = describeElement(target);
    const button = target?.closest('button, input, select, textarea') as HTMLButtonElement | null;
    // Computed BEFORE clickSeq advances: if anything here throws (e.g. a browser's elementsFromPoint
    // misbehaving), the click must not "consume" a sequence number it never actually recorded - seq
    // should always equal the count of clicks that made it into the buffer, not the count of attempts.
    const topElements = topElementStrings(event.clientX, event.clientY);

    this.clickSeq += 1;
    this.clickCount.set(this.clickSeq);

    this.push({
      type: 'click',
      seq: this.clickSeq,
      timestamp: Date.now(),
      route: pathOnly(this.router.url),
      targetTag: tag,
      targetClasses: classes,
      targetDisabled: !!button?.disabled,
      x: event.clientX,
      y: event.clientY,
      topElements,
      hasOverlay: hasBlockingOverlay(),
      activeElementTag: describeElement(document.activeElement).tag,
      visibilityState: document.visibilityState,
    });
  }

  private recordDiagnosticError(source: 'click', error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    // Still surfaced to the real console - this must never look like a silently swallowed exception to
    // anyone reading dev tools output, even though it's diagnostic-only code.
    console.error(`[BookSpace diagnostics] ${source} handler threw - this is a bug in the diagnostics tool itself, not the application:`, error);
    this.push({ type: 'diagnostic-error', timestamp: Date.now(), source, message });
  }

  // --- HTTP recording (called from diagnostics.interceptor.ts) --------------------------------------

  recordHttpStart(method: string, url: string): number {
    if (!this.enabled) {
      return -1;
    }
    const id = ++this.httpSeq;
    const path = pathOnly(url);
    this.activeHttp.set(id, { method, path, startedAt: performance.now(), settled: false });
    this.httpTotalCount.set(this.httpSeq);
    this.push({ type: 'http-start', timestamp: Date.now(), requestId: id, method, path });
    return id;
  }

  recordHttpOutcome(id: number, kind: 'http-success' | 'http-error', status: number | undefined, durationMs: number): void {
    if (!this.enabled || id < 0) {
      return;
    }
    const request = this.activeHttp.get(id);
    if (!request || request.settled) {
      return;
    }
    request.settled = true;
    this.activeHttp.delete(id);
    this.push({ type: kind, timestamp: Date.now(), requestId: id, method: request.method, path: request.path, status, durationMs });
  }

  // Called from the interceptor's finalize() - only actually records a 'http-cancel' event when neither
  // recordHttpOutcome success nor error already ran (i.e. the request was torn down mid-flight, such as
  // a component being destroyed while its request was still pending).
  recordHttpFinalize(id: number, durationMs: number): void {
    if (!this.enabled || id < 0) {
      return;
    }
    const request = this.activeHttp.get(id);
    if (!request) {
      return;
    }
    this.activeHttp.delete(id);
    this.push({ type: 'http-cancel', timestamp: Date.now(), requestId: id, method: request.method, path: request.path, durationMs });
  }

  // --- auth refresh recording (called from auth.service.ts) -----------------------------------------

  recordAuthRefresh(phase: AuthRefreshPhase): void {
    if (!this.enabled) {
      return;
    }
    this.authRefreshState.set(phase);
    this.push({ type: 'auth-refresh', timestamp: Date.now(), phase });
  }

  // --- snapshot / clipboard -----------------------------------------------------------------------

  snapshot(): DiagnosticsSnapshot {
    const now = performance.now();
    const activeHttpRequests: ActiveHttpRequestSnapshot[] = [...this.activeHttp.entries()].map(([requestId, request]) => ({
      requestId,
      method: request.method,
      path: request.path,
      elapsedMs: Math.round(now - request.startedAt),
    }));

    const centerX = window.innerWidth / 2;
    const centerY = window.innerHeight / 2;
    const centerViewportElements =
      typeof document.elementsFromPoint === 'function'
        ? document.elementsFromPoint(centerX, centerY).slice(0, 5).map(describeElementStyle)
        : [];

    const overlayElements = [...document.querySelectorAll(SNAPSHOT_ELEMENT_SELECTOR)].map(describeElementStyle);
    const shell = document.querySelector('.shell-content, .shell');
    const main = document.querySelector('main');

    return {
      generatedAt: new Date().toISOString(),
      events: [...this.buffer],
      currentRoute: pathOnly(this.router.url),
      activeHttpRequests,
      navigationState: this.navigationState(),
      authRefreshState: this.authRefreshState(),
      activeElement: describeElement(document.activeElement),
      centerViewportElements,
      overlayElements,
      overflow: {
        body: getComputedStyle(document.body).overflow,
        documentElement: getComputedStyle(document.documentElement).overflow,
        shell: shell ? getComputedStyle(shell).overflow : null,
        main: main ? getComputedStyle(main).overflow : null,
      },
      sidebarState: this.sidebarState(),
      viewport: { width: window.innerWidth, height: window.innerHeight },
    };
  }

  routerSnapshot(): RouterSnapshot {
    return {
      generatedAt: new Date().toISOString(),
      events: [...this.routerEventBuffer],
      browserUrl: pathOnly(location.href),
      routerUrl: pathOnly(this.router.url),
      lastNavigationStart: this.lastNavigationStart,
      lastNavigationEnd: this.lastNavigationEnd,
      lastNavigationCancel: this.lastNavigationCancel,
      lastNavigationError: this.lastNavigationError,
      navigationPending: this.navigationState() === 'running',
      shellOutletComponents: this.shellOutletComponents(),
    };
  }

  sidebarSnapshot(): SidebarSnapshot {
    const linkElements = [...document.querySelectorAll<HTMLAnchorElement>(`a${SIDEBAR_LINK_SELECTOR}`)];

    const links: SidebarLinkSnapshot[] = linkElements.map((link) => {
      const rect = link.getBoundingClientRect();
      const x = rect.left + rect.width / 2;
      const y = rect.top + rect.height / 2;
      const style = getComputedStyle(link);
      const elementAtPoint = typeof document.elementFromPoint === 'function' ? document.elementFromPoint(x, y) : null;
      const ancestors = describeAncestors(link, 'app-shell');

      return {
        text: link.textContent?.trim() ?? '',
        href: link.getAttribute('href'),
        rect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
        display: style.display,
        visibility: style.visibility,
        opacity: style.opacity,
        pointerEvents: style.pointerEvents,
        position: style.position,
        zIndex: style.zIndex,
        disabled: false, // <a> elements have no disabled state - the one disabled sidebar item is a <button>, never matched by `a.shell-nav__link`.
        isConnected: link.isConnected,
        elementAtCenter: elementAtPoint ? describeElement(elementAtPoint) : null,
        topElementsAtCenter: topElementStrings(x, y),
        topElementIsLinkOrDescendant: !!elementAtPoint && (elementAtPoint === link || link.contains(elementAtPoint)),
        ancestors,
        ancestorConcerns: ancestorConcerns(ancestors),
      };
    });

    const linkCountByHref: Record<string, number> = {};
    for (const link of linkElements) {
      const href = link.getAttribute('href') ?? '(no href)';
      linkCountByHref[href] = (linkCountByHref[href] ?? 0) + 1;
    }

    return {
      generatedAt: new Date().toISOString(),
      viewport: { width: window.innerWidth, height: window.innerHeight },
      links,
      sidebarContainer: describeContainer('.shell-sidebar'),
      navContainer: describeContainer('.shell-nav'),
      shellContainer: describeContainer('.shell'),
      mainContent: describeContainer('.shell-content'),
      appShellCount: document.querySelectorAll('app-shell').length,
      sidebarCount: document.querySelectorAll('.shell-sidebar').length,
      navContainerCount: document.querySelectorAll('.shell-nav').length,
      linkCountByHref,
    };
  }

  // The routed component currently active under <router-outlet> is rendered as a sibling element right
  // next to the outlet's own anchor comment, inside the same parent (.shell-content) - reading its
  // children's tag names is a DOM-level check of what's ACTUALLY rendered, independent of what any
  // component's own state claims, which is the point (see the task's "do not merely inspect component
  // Boolean values" instruction).
  private shellOutletComponents(): string[] {
    const main = document.querySelector('.shell-content');
    if (!main) {
      return [];
    }
    return [...main.children].map((el) => el.tagName.toLowerCase());
  }

  private sidebarState(): 'not-applicable' | 'open' | 'closed' {
    const sidebar = document.querySelector('.shell-sidebar');
    if (!sidebar) {
      return 'not-applicable';
    }
    const expanded = sidebar.getAttribute('aria-expanded') ?? sidebar.getAttribute('data-open');
    // This app's sidebar (see shell.html) is a static, always-visible element with no open/closed toggle
    // state today - "not-applicable" is the honest answer rather than guessing, in case a collapsible
    // sidebar is added later and starts setting one of the attributes above.
    return expanded === null ? 'not-applicable' : expanded === 'true' ? 'open' : 'closed';
  }

  async copy(): Promise<void> {
    const payload = JSON.stringify(this.snapshot(), null, 2);
    if (typeof navigator === 'undefined' || !navigator.clipboard) {
      this.logPayloadAsFallback(payload, 'Clipboard API unavailable');
      return;
    }
    try {
      await navigator.clipboard.writeText(payload);
    } catch (error) {
      // navigator.clipboard.writeText() requires the DOCUMENT (not just the DevTools panel) to have
      // focus - calling copy() right after clicking/typing in the DevTools console itself commonly hits
      // exactly this ("Document is not focused") rather than any real permission denial. Logging the raw
      // payload here means a failed clipboard write never costs the user the capture they just took -
      // it's still right there in the console to select/right-click-copy by hand.
      console.warn('[BookSpace diagnostics] Failed to write the snapshot to the clipboard - click the page (not DevTools) first, or copy the object below manually.', error);
      this.logPayloadAsFallback(payload, 'Snapshot (clipboard write failed)');
    }
  }

  private logPayloadAsFallback(payload: string, label: string): void {
    console.warn(`[BookSpace diagnostics] ${label} - here is the snapshot JSON to copy manually:`);
    console.log(payload);
  }

  private push(event: DiagnosticEvent): void {
    this.buffer.push(event);
    if (this.buffer.length > MAX_EVENTS) {
      this.buffer.shift();
    }
  }
}
