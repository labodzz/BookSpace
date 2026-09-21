import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

function mockMatchMedia(prefersDark: boolean): void {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: (query: string) => ({
      matches: query === '(prefers-color-scheme: dark)' && prefersDark,
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }),
  });
}

function removeMatchMedia(): void {
  Reflect.deleteProperty(window as unknown as object, 'matchMedia');
}

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    removeMatchMedia();
  });

  it('restores a saved light preference', () => {
    localStorage.setItem('bookspace.theme', 'light');
    mockMatchMedia(true); // OS says dark - the saved preference must still win

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('restores a saved dark preference', () => {
    localStorage.setItem('bookspace.theme', 'dark');
    mockMatchMedia(false); // OS says light - the saved preference must still win

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('uses the OS dark preference when nothing is saved', () => {
    mockMatchMedia(true);

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('dark');
  });

  it('uses the OS light preference when nothing is saved', () => {
    mockMatchMedia(false);

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('light');
  });

  it('falls back to the light default when nothing is saved and matchMedia is unavailable', () => {
    removeMatchMedia();

    expect(() => TestBed.inject(ThemeService)).not.toThrow();
    expect(TestBed.inject(ThemeService).theme()).toBe('light');
  });

  it('ignores an invalid stored value and falls back to the OS preference', () => {
    localStorage.setItem('bookspace.theme', 'blue');
    mockMatchMedia(true);

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('dark');
  });

  it('updates the signal when toggled', () => {
    mockMatchMedia(false);
    const service = TestBed.inject(ThemeService);

    service.toggle();
    expect(service.theme()).toBe('dark');

    service.toggle();
    expect(service.theme()).toBe('light');
  });

  it('updates the <html data-theme> attribute when toggled', () => {
    mockMatchMedia(false);
    const service = TestBed.inject(ThemeService);

    service.toggle();

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('persists the choice to localStorage when toggled', () => {
    mockMatchMedia(false);
    const service = TestBed.inject(ThemeService);

    service.toggle();

    expect(localStorage.getItem('bookspace.theme')).toBe('dark');
  });

  it('does not crash when localStorage is unavailable, and the theme still works for the rest of the session', () => {
    const originalLocalStorage = window.localStorage;
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: {
        getItem: () => {
          throw new Error('storage disabled');
        },
        setItem: () => {
          throw new Error('storage disabled');
        },
      },
    });

    try {
      let service!: ThemeService;
      expect(() => (service = TestBed.inject(ThemeService))).not.toThrow();
      expect(service.theme()).toBe('light');

      expect(() => service.toggle()).not.toThrow();
      expect(service.theme()).toBe('dark');
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    } finally {
      Object.defineProperty(window, 'localStorage', { configurable: true, value: originalLocalStorage });
    }
  });

  it('updates the active theme when another tab reports a storage change', () => {
    mockMatchMedia(false);
    const service = TestBed.inject(ThemeService);
    expect(service.theme()).toBe('light');

    window.dispatchEvent(new StorageEvent('storage', { key: 'bookspace.theme', newValue: 'dark', oldValue: 'light' }));

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('ignores a storage event for an unrelated key', () => {
    mockMatchMedia(false);
    const service = TestBed.inject(ThemeService);

    window.dispatchEvent(new StorageEvent('storage', { key: 'bookspace.accessToken', newValue: 'anything' }));

    expect(service.theme()).toBe('light');
  });
});
