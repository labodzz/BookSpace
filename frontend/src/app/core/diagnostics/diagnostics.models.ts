// Every field here is deliberately restricted to safe technical metadata - see diagnostics.service.ts's
// top-level comment for the full list of what must never appear in any of these shapes (tokens,
// passwords, headers, request/response bodies, personal data). Nothing in this file has a slot to put
// any of that in even by accident.

export type AuthRefreshPhase =
  | 'requested'
  | 'reusing-in-flight'
  | 'waiting-for-lock'
  | 'lock-acquired'
  | 'http-started'
  | 'completed'
  | 'failed'
  | 'timed-out'
  | 'lock-released'
  | 'in-flight-cleared';

export interface ClickDiagnosticEvent {
  type: 'click';
  seq: number;
  timestamp: number;
  route: string;
  targetTag: string;
  targetClasses: string;
  targetDisabled: boolean;
  x: number;
  y: number;
  topElements: string[];
  hasOverlay: boolean;
  activeElementTag: string;
  visibilityState: DocumentVisibilityState;
}

// RouteConfigLoadStart/End (bracketing a lazy loadComponent() import()) carry no navigation id of their
// own - Angular's own event shape, not an omission here - so navigationId is null for exactly those two.
export type RouterEventKind =
  | 'nav-start'
  | 'route-config-load-start'
  | 'route-config-load-end'
  | 'guards-check-start'
  | 'guards-check-end'
  | 'resolve-start'
  | 'resolve-end'
  | 'nav-cancel'
  | 'nav-error'
  | 'nav-end';

export interface RouterDiagnosticEvent {
  type: RouterEventKind;
  timestamp: number;
  navigationId: number | null;
  path: string;
  // NavigationCancel only. `reason` is the Router's own short internal description (e.g. "Navigation ID
  // 4 is not equal to the current navigation id 5") - never user input, safe to keep as-is.
  cancellationCode?: string;
  reason?: string;
  // NavigationError only - the error's message text alone (see diagnostics.service.ts's sanitizeError),
  // never the raw error/event object, which could otherwise carry anything.
  error?: string;
}

export type HttpEventKind = 'http-start' | 'http-success' | 'http-error' | 'http-cancel';

export interface HttpDiagnosticEvent {
  type: HttpEventKind;
  timestamp: number;
  requestId: number;
  method: string;
  path: string;
  status?: number;
  durationMs?: number;
}

export interface AuthRefreshDiagnosticEvent {
  type: 'auth-refresh';
  timestamp: number;
  phase: AuthRefreshPhase;
}

export interface LongTaskDiagnosticEvent {
  type: 'long-task';
  timestamp: number;
  startTime: number;
  duration: number;
}

// Self-reporting: if handleClick() ever throws, it's recorded here instead of silently vanishing - see
// diagnostics.service.ts's try/catch. Proves whether the listener keeps working on the NEXT event after
// a throw (native addEventListener callbacks are independent per-invocation - one throwing does not
// unregister the listener) rather than just asserting it.
export interface DiagnosticErrorEvent {
  type: 'diagnostic-error';
  timestamp: number;
  source: 'click';
  message: string;
}

export type DiagnosticEvent =
  | ClickDiagnosticEvent
  | RouterDiagnosticEvent
  | HttpDiagnosticEvent
  | AuthRefreshDiagnosticEvent
  | LongTaskDiagnosticEvent
  | DiagnosticErrorEvent;

export interface ActiveHttpRequestSnapshot {
  requestId: number;
  method: string;
  path: string;
  elapsedMs: number;
}

export interface ElementSnapshot {
  tag: string;
  classes: string;
  display: string;
  visibility: string;
  opacity: string;
  pointerEvents: string;
  position: string;
  zIndex: string;
}

export interface DiagnosticsSnapshot {
  generatedAt: string;
  events: DiagnosticEvent[];
  currentRoute: string;
  activeHttpRequests: ActiveHttpRequestSnapshot[];
  navigationState: 'idle' | 'running';
  authRefreshState: AuthRefreshPhase | 'idle';
  activeElement: { tag: string; classes: string };
  centerViewportElements: ElementSnapshot[];
  overlayElements: ElementSnapshot[];
  overflow: {
    body: string;
    documentElement: string;
    shell: string | null;
    main: string | null;
  };
  sidebarState: 'not-applicable' | 'open' | 'closed';
  viewport: { width: number; height: number };
}

export interface AncestorStyleSnapshot {
  tag: string;
  classes: string;
  pointerEvents: string;
  visibility: string;
  display: string;
  position: string;
  zIndex: string;
  transform: string;
  overflow: string;
  inert: boolean;
}

export interface Rect {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface SidebarLinkSnapshot {
  text: string;
  href: string | null;
  rect: Rect;
  display: string;
  visibility: string;
  opacity: string;
  pointerEvents: string;
  position: string;
  zIndex: string;
  disabled: boolean;
  isConnected: boolean;
  elementAtCenter: { tag: string; classes: string } | null;
  topElementsAtCenter: string[];
  // True when document.elementFromPoint() at this link's own center returns the link itself or a
  // descendant of it (its text node's parent span, an icon inside it, etc.) - false means something ELSE
  // is genuinely topmost there, which is the actual hit-testing failure this whole function exists to
  // catch. See the task's own interpretation table for what a false here means.
  topElementIsLinkOrDescendant: boolean;
  // Every ancestor from the link up to (and including) app-shell, closest first.
  ancestors: AncestorStyleSnapshot[];
  // Human-readable flags for anything found among `ancestors` that could plausibly block a click:
  // pointer-events:none, visibility:hidden, display:none, a non-visible overflow, a transform (creates a
  // new stacking/containing-block context), or the inert attribute. Empty when nothing was found.
  ancestorConcerns: string[];
}

export interface ContainerSnapshot {
  found: boolean;
  tag: string;
  classes: string;
  rect: Rect | null;
  pointerEvents: string | null;
  position: string | null;
  zIndex: string | null;
  overflow: string | null;
  transform: string | null;
}

export interface SidebarSnapshot {
  generatedAt: string;
  viewport: { width: number; height: number };
  links: SidebarLinkSnapshot[];
  sidebarContainer: ContainerSnapshot;
  navContainer: ContainerSnapshot;
  shellContainer: ContainerSnapshot;
  mainContent: ContainerSnapshot;
  // DOM identity counts - see ShellComponent's "should remain mounted, not recreated" invariant.
  appShellCount: number;
  sidebarCount: number;
  navContainerCount: number;
  linkCountByHref: Record<string, number>;
}

export interface RouterSnapshot {
  generatedAt: string;
  events: RouterDiagnosticEvent[];
  browserUrl: string;
  routerUrl: string;
  lastNavigationStart: RouterDiagnosticEvent | null;
  lastNavigationEnd: RouterDiagnosticEvent | null;
  lastNavigationCancel: RouterDiagnosticEvent | null;
  lastNavigationError: RouterDiagnosticEvent | null;
  navigationPending: boolean;
  shellOutletComponents: string[];
}
