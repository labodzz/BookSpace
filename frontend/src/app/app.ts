import { Component, HostListener, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { environment } from '../environments/environment';
import { DiagnosticsIndicatorComponent } from './core/diagnostics/diagnostics-indicator/diagnostics-indicator';
import { DiagnosticsService } from './core/diagnostics/diagnostics.service';
import { ToastComponent } from './core/notifications/toast/toast';

@Component({
  imports: [RouterOutlet, ToastComponent, DiagnosticsIndicatorComponent],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  // Eagerly injected (never read directly) purely so the service's own click/router/HTTP/auth-refresh
  // listeners are registered from app startup - nothing here ever calls it, DiagnosticsIndicatorComponent
  // is what actually reads its signals for display.
  private readonly diagnostics = inject(DiagnosticsService);

  protected readonly isProduction = environment.production;

  // Centralized fix for the native browser link-drag freeze (pointerdown -> mousedown -> dragstart ->
  // pointercancel, leaving the page stuck) originally found and fixed narrowly on the sidebar (draggable="false"
  // in shell.html/shell.scss) and later reproduced on the Users list row - any plain <a> is natively
  // draggable, and every internal navigation link in this app (routerLink or a hardcoded href="/...") is a
  // plain <a>, so the same freeze can happen from any of them, not just the sidebar.
  //
  // Bound once here via @HostListener rather than per-component: dragstart bubbles like any other DOM
  // event, and <app-root> is the single ancestor every anchor in the app renders inside, so one listener
  // here sees every drag attempt anywhere in the app. Angular creates exactly one App instance for the
  // lifetime of the page and manages this listener's registration/cleanup with it - no manual
  // addEventListener/removeEventListener, no risk of duplicate listeners across navigations.
  //
  // Anchors are matched by same-origin http(s) href, not by Angular's ng-reflect-router-link (a
  // development-only attribute that doesn't exist in production builds) or a shared marker directive -
  // this also means a future plain internal <a href="/..."> is protected automatically, without needing to
  // remember to opt it in.
  @HostListener('dragstart', ['$event'])
  protected onInternalLinkDragStart(event: DragEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
      return;
    }

    const anchor = target.closest('a');
    if (!anchor || !anchor.hasAttribute('href') || typeof window === 'undefined') {
      return;
    }

    let url: URL;
    try {
      // anchor.href (unlike getAttribute('href')) is always the browser-resolved absolute URL, so no base
      // is needed here even though routerLink renders a root-relative href like "/users/123".
      url = new URL(anchor.href);
    } catch {
      return;
    }

    const isHttpOrHttps = url.protocol === 'http:' || url.protocol === 'https:';
    if (isHttpOrHttps && url.origin === window.location.origin) {
      event.preventDefault();
    }
  }
}
