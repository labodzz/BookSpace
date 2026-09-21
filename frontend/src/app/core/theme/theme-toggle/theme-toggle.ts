import { Component, computed, inject } from '@angular/core';
import { ThemeService } from '../theme.service';

// The one reusable control for switching light/dark theme - used in the authenticated shell and on the
// public Welcome/Login pages, so the toggle behavior lives in exactly one place instead of being
// re-implemented per page. Never touches the DOM itself; it only ever calls ThemeService.toggle(),
// which is the single place that updates the signal, localStorage, and <html data-theme> together.
@Component({
  selector: 'app-theme-toggle',
  imports: [],
  templateUrl: './theme-toggle.html',
  styleUrl: './theme-toggle.scss',
})
export class ThemeToggleComponent {
  private readonly themeService = inject(ThemeService);

  protected readonly isDark = computed(() => this.themeService.theme() === 'dark');

  protected toggle(): void {
    this.themeService.toggle();
  }
}
