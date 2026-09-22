import { Component, inject } from '@angular/core';
import { DiagnosticsService } from '../diagnostics.service';

// TEMPORARY, development-only. Rendered only when !environment.production (see app.html/app.ts) - purely
// a live readout of DiagnosticsService's own signals, so it lets a developer see, at a glance, which of
// the four counters stops moving the moment a freeze happens (see the "Interpretation" guidance handed
// back with this instrumentation).
@Component({
  selector: 'app-diagnostics-indicator',
  imports: [],
  templateUrl: './diagnostics-indicator.html',
  styleUrl: './diagnostics-indicator.scss',
})
export class DiagnosticsIndicatorComponent {
  protected readonly diagnostics = inject(DiagnosticsService);
}
