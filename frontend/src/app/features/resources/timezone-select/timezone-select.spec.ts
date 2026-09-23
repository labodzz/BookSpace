import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../../environments/environment';
import { TimezoneSelectComponent } from './timezone-select';

const TIMEZONES_URL = `${environment.apiUrl}/resources/supported-timezones`;
const SUPPORTED_TIMEZONES = ['America/New_York', 'Asia/Tokyo', 'Europe/London', 'Europe/Sarajevo', 'UTC'];

describe('TimezoneSelectComponent', () => {
  let fixture: ComponentFixture<TimezoneSelectComponent>;
  let httpTesting: HttpTestingController;

  function configure(initialValue: string): void {
    TestBed.configureTestingModule({
      imports: [TimezoneSelectComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(TimezoneSelectComponent);
    fixture.componentRef.setInput('value', initialValue);
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function input(): HTMLInputElement {
    return root().querySelector('input[role="combobox"]') as HTMLInputElement;
  }

  function options(): HTMLElement[] {
    return [...root().querySelectorAll('[role="option"]')] as HTMLElement[];
  }

  function flushZones(zones: string[] = SUPPORTED_TIMEZONES): void {
    httpTesting.expectOne(TIMEZONES_URL).flush(zones);
    fixture.detectChanges();
  }

  function openAndType(text: string): void {
    input().dispatchEvent(new Event('focus'));
    fixture.detectChanges();
    input().value = text;
    input().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function subscribeToValueChange(): { value: string | undefined } {
    const captured: { value: string | undefined } = { value: undefined };
    fixture.componentInstance.valueChange.subscribe((v) => (captured.value = v));
    return captured;
  }

  afterEach(() => httpTesting.verify());

  describe('loading and error states', () => {
    it('shows a loading state while the supported list is being fetched', () => {
      configure('UTC');
      fixture.detectChanges();

      expect(root().textContent).toContain('Loading time zones');
      httpTesting.expectOne(TIMEZONES_URL).flush(SUPPORTED_TIMEZONES);
    });

    it('shows a retry button on a failed fetch, and Retry re-issues the request and recovers', () => {
      configure('UTC');
      fixture.detectChanges();
      httpTesting.expectOne(TIMEZONES_URL).flush('boom', { status: 500, statusText: 'Internal Server Error' });
      fixture.detectChanges();

      expect(root().textContent).toContain("Couldn't load");
      expect(root().querySelector('input[role="combobox"]')).toBeNull();

      const retryButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Retry')!;
      retryButton.click();
      fixture.detectChanges();
      flushZones();

      expect(root().querySelector('input[role="combobox"]')).not.toBeNull();
    });
  });

  describe('browsing and searching', () => {
    it('opening the combobox (without typing) shows the full supported list to browse', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      input().dispatchEvent(new Event('focus'));
      fixture.detectChanges();

      expect(options().map((o) => o.textContent?.trim())).toEqual(expect.arrayContaining(SUPPORTED_TIMEZONES));
    });

    it('searches by the complete IANA id', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      openAndType('Europe/Sarajevo');

      expect(options().map((o) => o.textContent?.trim())).toEqual(['Europe/Sarajevo']);
    });

    it('searches case-insensitively by a partial city name, matching an underscore as a space', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      openAndType('nEw yOrK');

      expect(options().map((o) => o.textContent?.trim())).toEqual(['America/New_York']);
    });

    it('searches by region name, returning every match', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      openAndType('europe');

      expect(options().map((o) => o.textContent?.trim())).toEqual(['Europe/London', 'Europe/Sarajevo']);
    });

    it('shows a "no matching time zones" state for a search with no results', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      openAndType('Nowhereland');

      expect(root().textContent).toContain('No matching time zones');
      expect(options().length).toBe(0);
    });

    it('keeps a stable, correct option list across repeated change-detection passes with no query change', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();
      openAndType('Europe');

      const firstPass = options().map((o) => o.textContent?.trim());
      fixture.detectChanges();
      fixture.detectChanges();
      fixture.detectChanges();
      const laterPass = options().map((o) => o.textContent?.trim());

      expect(laterPass).toEqual(firstPass);
      expect(laterPass).toEqual(['Europe/London', 'Europe/Sarajevo']);
    });
  });

  describe('selection', () => {
    it('emits the selected zone on mousedown and closes the dropdown', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();
      const captured = subscribeToValueChange();

      openAndType('Tokyo');
      options()[0].dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
      fixture.detectChanges();

      expect(captured.value).toBe('Asia/Tokyo');
      expect(input().getAttribute('aria-expanded')).toBe('false');
    });

    it('cannot submit arbitrary typed text - only a real selection (or the untouched original value) ever emits', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();
      const captured = subscribeToValueChange();

      openAndType('this is not a real timezone');
      input().dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      expect(captured.value).toBeUndefined();
      expect(input().value).toBe('UTC');
    });
  });

  describe('keyboard navigation', () => {
    it('ArrowDown opens the list, moves the active option, and Enter selects it', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();
      const captured = subscribeToValueChange();

      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' }));
      fixture.detectChanges();
      expect(input().getAttribute('aria-expanded')).toBe('true');

      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' }));
      fixture.detectChanges();
      const activeId = input().getAttribute('aria-activedescendant');
      expect(activeId).toBeTruthy();
      expect(root().querySelector(`#${activeId}`)?.classList.contains('timezone-select__option--active')).toBe(true);

      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      fixture.detectChanges();

      expect(captured.value).toBeTruthy();
      expect(input().getAttribute('aria-expanded')).toBe('false');
    });

    it('ArrowUp wraps around to the last option from the first', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' })); // open, active = 0
      fixture.detectChanges();
      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp' })); // wrap to last
      fixture.detectChanges();

      const activeId = input().getAttribute('aria-activedescendant');
      const activeText = root().querySelector(`#${activeId}`)?.textContent?.trim();
      expect(activeText).toBe(options()[options().length - 1].textContent?.trim());
    });

    it('Escape closes the list without changing the value', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();
      const captured = subscribeToValueChange();

      openAndType('Tokyo');
      input().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();

      expect(input().getAttribute('aria-expanded')).toBe('false');
      expect(captured.value).toBeUndefined();
      expect(input().value).toBe('UTC');
    });
  });

  describe('Create default detection (autoDetectBrowserTimezone)', () => {
    afterEach(() => vi.restoreAllMocks());

    it('preselects the browser time zone when it is in the supported list', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
        timeZone: 'Asia/Tokyo',
      } as Intl.ResolvedDateTimeFormatOptions);
      configure('');
      fixture.componentRef.setInput('autoDetectBrowserTimezone', true);
      fixture.detectChanges();
      const captured = subscribeToValueChange();

      flushZones();

      expect(captured.value).toBe('Asia/Tokyo');
    });

    it('falls back to the project default (Europe/Sarajevo) when the browser zone is not supported', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
        timeZone: 'Pacific/Nowhere',
      } as unknown as Intl.ResolvedDateTimeFormatOptions);
      configure('');
      fixture.componentRef.setInput('autoDetectBrowserTimezone', true);
      fixture.detectChanges();
      const captured = subscribeToValueChange();

      flushZones();

      expect(captured.value).toBe('Europe/Sarajevo');
    });

    it('leaves the value unset (forcing an explicit choice) if neither the browser zone nor the fallback is supported', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
        timeZone: 'Pacific/Nowhere',
      } as unknown as Intl.ResolvedDateTimeFormatOptions);
      configure('');
      fixture.componentRef.setInput('autoDetectBrowserTimezone', true);
      fixture.detectChanges();
      const captured = subscribeToValueChange();

      flushZones(['America/New_York']); // no Europe/Sarajevo in this particular list either

      expect(captured.value).toBeUndefined();
    });

    it('falls back gracefully (to the project default) when the browser cannot report a time zone at all', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockImplementation(() => {
        throw new Error('Intl unavailable');
      });
      configure('');
      fixture.componentRef.setInput('autoDetectBrowserTimezone', true);
      fixture.detectChanges();
      const captured = subscribeToValueChange();

      flushZones();

      expect(captured.value).toBe('Europe/Sarajevo');
    });

    it('does not auto-detect at all when autoDetectBrowserTimezone is false (Edit mode) - a legacy value stays untouched', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({
        timeZone: 'Asia/Tokyo',
      } as Intl.ResolvedDateTimeFormatOptions);
      configure('Legacy/Value');
      fixture.detectChanges(); // autoDetectBrowserTimezone defaults to false
      const captured = subscribeToValueChange();

      flushZones();

      expect(captured.value).toBeUndefined();
      expect(input().value).toBe('Legacy/Value');
    });
  });

  describe('legacy stored values', () => {
    it('displays a value not in the supported list as-is, with a warning, without clearing it', () => {
      configure('Eastern Standard Time');
      fixture.detectChanges();
      flushZones();

      expect(input().value).toBe('Eastern Standard Time');
      expect(root().textContent).toContain("isn't in the current supported list");
    });

    it('shows no legacy warning for a value that IS in the supported list', () => {
      configure('UTC');
      fixture.detectChanges();
      flushZones();

      expect(root().textContent).not.toContain("isn't in the current supported list");
    });
  });

  it('shows the current UTC offset as purely informational text next to a selected value', () => {
    configure('Europe/Sarajevo');
    fixture.detectChanges();
    flushZones();

    expect(root().textContent).toMatch(/Currently UTC[+-]\d{2}:\d{2}/);
  });

  it('renders with the same structure regardless of the active theme (colors are token-driven, not conditional markup)', () => {
    configure('UTC');
    fixture.detectChanges();
    flushZones();

    document.documentElement.setAttribute('data-theme', 'dark');
    fixture.detectChanges();
    expect(root().querySelector('.timezone-select')).not.toBeNull();
    expect(root().querySelector('input[role="combobox"]')).not.toBeNull();
    document.documentElement.removeAttribute('data-theme');
  });

  it('cleans up its document click listener on destroy - closing via outside-click never fires after that', () => {
    configure('UTC');
    fixture.detectChanges();
    flushZones();
    input().dispatchEvent(new Event('focus'));
    fixture.detectChanges();
    expect(input().getAttribute('aria-expanded')).toBe('true');

    fixture.destroy();

    expect(() => document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }))).not.toThrow();
  });
});
