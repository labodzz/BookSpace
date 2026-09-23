import { Component, inject } from '@angular/core';
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
}
