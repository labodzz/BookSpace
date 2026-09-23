import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { ApiError, toApiError } from '../../../core/http/api-error';
import { ResourceService } from '../resource.service';
import { formatCurrentUtcOffset, matchesTimeZoneQuery } from '../timezone.util';

// The project's own documented default for a fresh resource whose administrator hasn't picked (and
// whose browser doesn't report) a supported zone - see docs/resource-lifecycle-and-capacity.md. Used
// only as a last-resort Create default, never silently substituted for an Edit resource's own value.
const FALLBACK_DEFAULT_ZONE = 'Europe/Sarajevo';

// Caps the rendered option list so opening the combobox never renders all ~400 supported zones at
// once - filtering still runs over the full cached list (see timezone.util.ts's matchesTimeZoneQuery),
// only rendering is capped.
const MAX_VISIBLE_RESULTS = 50;

let nextInstanceId = 0;

// Reusable, accessible searchable combobox for picking a resource's IANA time zone - shared by Create
// and Edit Resource (see resource-form.html) so the option list, search, and keyboard behavior are
// defined exactly once. Options come from GET /resources/supported-timezones (ResourceService.
// getSupportedTimeZones(), cached for the app's lifetime) - the same list the backend's own
// Create/UpdateResourceCommandRequest validator accepts, so a selection here can never be rejected as
// "unsupported" by the server.
@Component({
  selector: 'app-timezone-select',
  imports: [],
  templateUrl: './timezone-select.html',
  styleUrl: './timezone-select.scss',
})
export class TimezoneSelectComponent {
  private readonly resourceService = inject(ResourceService);
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  // The resource's current value (canonical IANA id, or a legacy string not in the supported list) -
  // owned by the parent form, never mutated locally; a selection is only ever proposed via valueChange.
  readonly value = input.required<string>();
  readonly valueChange = output<string>();
  // True only for Create - see the constructor effect below. Edit always leaves the resource's own
  // stored value alone, regardless of this input, because the parent only ever passes true for Create.
  readonly autoDetectBrowserTimezone = input(false);

  protected readonly fieldId = 'time-zone';
  protected readonly listboxId = `timezone-select-listbox-${nextInstanceId++}`;
  protected readonly maxVisibleResults = MAX_VISIBLE_RESULTS;

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly zones = signal<string[]>([]);

  protected readonly isOpen = signal(false);
  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);

  private hasAutoDetected = false;

  // Computed once per (zones, query) change, not per keystroke's worth of renders and not rebuilt by
  // unrelated change detection - both filteredZones and hasMoreResults read this same memoized result
  // instead of each re-filtering the full list independently.
  private readonly matchResult = computed(() => {
    const searchQuery = this.query();
    const allZones = this.zones();
    const matches = searchQuery.trim() ? allZones.filter((zone) => matchesTimeZoneQuery(zone, searchQuery)) : allZones;
    return { matches: matches.slice(0, MAX_VISIBLE_RESULTS), total: matches.length };
  });

  protected readonly filteredZones = computed(() => this.matchResult().matches);
  protected readonly hasMoreResults = computed(() => this.matchResult().total > MAX_VISIBLE_RESULTS);

  // While the dropdown is open, the field shows the in-progress search text; closed, it shows the
  // actual current value - so leaving a search unfinished (blur/Escape without selecting) can never
  // submit the in-progress typed text, only ever a real selection or the untouched original value.
  protected readonly displayText = computed(() => (this.isOpen() ? this.query() : this.value()));

  protected readonly isLegacyValue = computed(() => {
    const currentValue = this.value();
    return currentValue !== '' && this.zones().length > 0 && !this.zones().includes(currentValue);
  });

  protected readonly currentOffsetLabel = computed(() => {
    const currentValue = this.value();
    return currentValue ? formatCurrentUtcOffset(currentValue) : null;
  });

  constructor() {
    this.fetchZones();
  }

  @HostListener('document:click', ['$event'])
  protected onDocumentClick(event: MouseEvent): void {
    if (this.isOpen() && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.closeDropdown();
    }
  }

  protected retryLoad(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.fetchZones();
  }

  protected onFocus(): void {
    this.openDropdown();
  }

  protected onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.isOpen.set(true);
    this.activeIndex.set(0);
  }

  protected onBlur(): void {
    this.closeDropdown();
  }

  protected onKeydown(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.isOpen() ? this.moveActive(1) : this.openDropdown();
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.isOpen() ? this.moveActive(-1) : this.openDropdown();
        break;
      case 'Home':
        if (this.isOpen()) {
          event.preventDefault();
          this.activeIndex.set(0);
        }
        break;
      case 'End':
        if (this.isOpen()) {
          event.preventDefault();
          this.activeIndex.set(Math.max(0, this.filteredZones().length - 1));
        }
        break;
      case 'Enter': {
        if (!this.isOpen()) {
          break;
        }
        event.preventDefault();
        const active = this.filteredZones()[this.activeIndex()];
        if (active) {
          this.selectZone(active);
        }
        break;
      }
      case 'Escape':
        if (this.isOpen()) {
          event.preventDefault();
          this.closeDropdown();
        }
        break;
    }
  }

  // mousedown (not click) fires before the input's blur - paired with preventDefault() this keeps
  // focus on the input instead of letting blur close the dropdown before the selection registers.
  protected selectZone(zone: string, event?: Event): void {
    event?.preventDefault();
    this.valueChange.emit(zone);
    this.closeDropdown();
  }

  protected optionId(index: number): string {
    return `${this.listboxId}-option-${index}`;
  }

  private openDropdown(): void {
    if (this.loading() || this.loadError()) {
      return;
    }
    this.isOpen.set(true);
    this.query.set('');
    this.activeIndex.set(0);
  }

  private closeDropdown(): void {
    this.isOpen.set(false);
    this.query.set('');
  }

  private moveActive(delta: number): void {
    const count = this.filteredZones().length;
    if (count === 0) {
      this.activeIndex.set(0);
      return;
    }
    this.activeIndex.update((current) => (current + delta + count) % count);
  }

  private fetchZones(): void {
    this.resourceService.getSupportedTimeZones().subscribe({
      next: (zones) => {
        this.zones.set(zones);
        this.loading.set(false);
        this.maybeAutoDetect();
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.loadError.set(error instanceof HttpErrorResponse ? toApiError(error) : { status: 0, title: 'Something went wrong.' });
      },
    });
  }

  // Runs at most once per component instance, only for Create (autoDetectBrowserTimezone=true) and only
  // once the supported list has actually loaded - never re-runs on a later zones() change, so it can
  // never stomp a value the administrator already picked (or the resource's own stored value on Edit,
  // which never sets autoDetectBrowserTimezone in the first place - see resource-form.html).
  private maybeAutoDetect(): void {
    if (this.hasAutoDetected || !this.autoDetectBrowserTimezone() || this.zones().length === 0) {
      return;
    }
    this.hasAutoDetected = true;

    const browserZone = this.detectBrowserTimeZone();
    if (browserZone && this.zones().includes(browserZone)) {
      this.valueChange.emit(browserZone);
    } else if (this.zones().includes(FALLBACK_DEFAULT_ZONE)) {
      this.valueChange.emit(FALLBACK_DEFAULT_ZONE);
    }
    // Otherwise leave the value exactly as the parent initialized it (empty) - the form's own required
    // validation then forces an explicit choice, rather than guessing further.
  }

  private detectBrowserTimeZone(): string | null {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
    } catch {
      return null;
    }
  }
}
