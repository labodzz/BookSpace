import { Injectable, computed, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'bookspace.theme';
const THEME_ATTRIBUTE = 'data-theme';

// The one theme every environment falls back to when there is no saved preference, no OS preference
// signal, or storage/matchMedia simply are not available (unit tests, privacy modes that disable Web
// Storage) - matches styles.scss's own default, un-attributed `:root` palette. Kept in sync with the
// inline bootstrap script in index.html, which resolves the same way before Angular loads.
export const DEFAULT_THEME: Theme = 'light';

function parseTheme(value: string | null): Theme | null {
  return value === 'light' || value === 'dark' ? value : null;
}

// The single owner of the app's light/dark theme - mirrored to localStorage (so it survives reloads,
// logins/logouts, and brand-new tabs) and to <html data-theme="..."> (so every stylesheet, global and
// per-component, can react to it purely through the --color-* custom properties in styles.scss, without
// any component touching the DOM directly). This is the same "plain injectable service holding a
// signal" shape as AuthService/CalendarService - no NgRx, no separate store, just a signal scoped to
// the one concern that owns it.
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly themeSignal = signal<Theme>(this.resolveInitialTheme());

  // Exposed as a computed, not the raw writable signal, so nothing outside this service can move the
  // app to a new theme except through setTheme()/toggle() - the only two paths that also persist the
  // choice and update the DOM attribute in the same step, keeping the signal, storage, and the DOM
  // attribute from ever drifting apart.
  readonly theme = computed(() => this.themeSignal());

  constructor() {
    // Re-applies the already-resolved theme to the DOM. index.html's inline script already set the
    // same attribute before Angular bootstrapped (to avoid a flash of the wrong theme); this second,
    // idempotent write is what keeps every later reactive update flowing through this one method rather
    // than having two separate places that touch document.documentElement.
    this.applyThemeToDocument(this.themeSignal());

    if (typeof window !== 'undefined') {
      // Cross-tab sync: the browser's `storage` event fires in every OTHER tab of this browser (never
      // the tab that made the change) - the same mechanism TokenStorageService's session persistence
      // relies on tabs discovering on their own - so a preference picked in one tab reaches every other
      // open tab immediately, without a reload and without introducing BroadcastChannel for this one
      // feature.
      window.addEventListener('storage', (event) => this.onStorageEvent(event));
    }
  }

  setTheme(theme: Theme): void {
    this.themeSignal.set(theme);
    this.applyThemeToDocument(theme);
    this.persist(theme);
  }

  toggle(): void {
    this.setTheme(this.themeSignal() === 'dark' ? 'light' : 'dark');
  }

  private resolveInitialTheme(): Theme {
    return this.readStoredTheme() ?? (this.prefersDarkColorScheme() ? 'dark' : DEFAULT_THEME);
  }

  private readStoredTheme(): Theme | null {
    try {
      return typeof localStorage === 'undefined' ? null : parseTheme(localStorage.getItem(STORAGE_KEY));
    } catch {
      // Storage disabled or unavailable (private browsing, some test/server-like environments) - fall
      // back to the OS preference below rather than letting a thrown error break app bootstrap.
      return null;
    }
  }

  private prefersDarkColorScheme(): boolean {
    try {
      return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
    } catch {
      return false;
    }
  }

  private persist(theme: Theme): void {
    try {
      if (typeof localStorage !== 'undefined') {
        localStorage.setItem(STORAGE_KEY, theme);
      }
    } catch {
      // The signal and the DOM attribute are already updated above, so the chosen theme still works
      // for the rest of this session even if it will not survive a reload.
    }
  }

  private applyThemeToDocument(theme: Theme): void {
    if (typeof document === 'undefined') {
      return;
    }

    document.documentElement.setAttribute(THEME_ATTRIBUTE, theme);
  }

  // Only reacts to OTHER tabs changing bookspace.theme (event.key/newValue reflect the write that just
  // happened elsewhere) - this tab's own writes go through setTheme() above and never round-trip
  // through this handler. A missing/invalid newValue (the key was removed, or holds something this
  // version of the app doesn't recognize) falls back the same way initial resolution does, rather than
  // leaving this tab on a value nothing can explain.
  private onStorageEvent(event: StorageEvent): void {
    if (event.key !== STORAGE_KEY) {
      return;
    }

    const theme = parseTheme(event.newValue) ?? (this.prefersDarkColorScheme() ? 'dark' : DEFAULT_THEME);
    this.themeSignal.set(theme);
    this.applyThemeToDocument(theme);
  }
}
