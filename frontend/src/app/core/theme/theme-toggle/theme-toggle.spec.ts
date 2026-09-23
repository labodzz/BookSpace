import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ThemeToggleComponent } from './theme-toggle';

describe('ThemeToggleComponent', () => {
  let fixture: ComponentFixture<ThemeToggleComponent>;

  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({ imports: [ThemeToggleComponent] });
    fixture = TestBed.createComponent(ThemeToggleComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  function button(): HTMLButtonElement {
    return (fixture.nativeElement as HTMLElement).querySelector('button') as HTMLButtonElement;
  }

  // jsdom has no matchMedia, so with no saved preference ThemeService's own default (light) applies -
  // giving this test a deterministic starting point without needing to mock ThemeService itself.
  it('starts as a keyboard-operable button with an accessible name reflecting the light theme', () => {
    const btn = button();

    expect(btn.tagName).toBe('BUTTON');
    expect(btn.getAttribute('type')).toBe('button');
    expect(btn.getAttribute('aria-pressed')).toBe('false');
    expect(btn.getAttribute('aria-label')).toMatch(/dark/i);
    expect(btn.textContent).toContain('Light');
  });

  it('switches to dark - updating its own label, aria-pressed, and the document root - when clicked', () => {
    button().click();
    fixture.detectChanges();

    const btn = button();
    expect(btn.getAttribute('aria-pressed')).toBe('true');
    expect(btn.getAttribute('aria-label')).toMatch(/light/i);
    expect(btn.textContent).toContain('Dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('renders a different icon per theme rather than only changing color', () => {
    const firstIconMarkup = button().querySelector('.theme-toggle__icon')?.innerHTML;

    button().click();
    fixture.detectChanges();

    const secondIconMarkup = button().querySelector('.theme-toggle__icon')?.innerHTML;
    expect(secondIconMarkup).not.toBe(firstIconMarkup);
  });

  it('toggles back to light on a second click', () => {
    button().click();
    fixture.detectChanges();
    button().click();
    fixture.detectChanges();

    expect(button().getAttribute('aria-pressed')).toBe('false');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });
});
