import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OwnBooking } from '../../booking.models';
import { BookingRowComponent } from './booking-row';

function wireBooking(overrides: Partial<OwnBooking> = {}): OwnBooking {
  return {
    id: 'booking-1',
    resourceId: 'resource-1',
    startUtc: '2026-12-20T10:00:00Z',
    endUtc: '2026-12-20T11:00:00Z',
    quantity: 1,
    status: 'Confirmed',
    cancelledAtUtc: null,
    cancelledByAdmin: false,
    cancellationReason: null,
    seriesId: null,
    timeZoneId: 'UTC',
    checkedInAtUtc: null,
    checkInWindowOpensAtUtc: '2026-12-20T09:45:00Z',
    checkInWindowClosesAtUtc: '2026-12-20T10:15:00Z',
    ...overrides,
  };
}

describe('BookingRowComponent', () => {
  let fixture: ComponentFixture<BookingRowComponent>;

  function configure(inputs: { booking: OwnBooking; resourceName?: string; compact?: boolean }): void {
    TestBed.configureTestingModule({ imports: [BookingRowComponent] });
    fixture = TestBed.createComponent(BookingRowComponent);
    fixture.componentRef.setInput('booking', inputs.booking);
    fixture.componentRef.setInput('resourceName', inputs.resourceName ?? 'Falcon Room');
    if (inputs.compact !== undefined) {
      fixture.componentRef.setInput('compact', inputs.compact);
    }
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the resource name and a "recurring" badge for a series occurrence by default', () => {
    configure({ booking: wireBooking({ seriesId: 'series-1' }) });

    expect(root().textContent).toContain('Falcon Room');
    expect(root().textContent).toContain('recurring');
  });

  it('hides the resource name and "recurring" badge when compact, keeping the time and status', () => {
    configure({ booking: wireBooking({ seriesId: 'series-1' }), compact: true });

    expect(root().textContent).not.toContain('Falcon Room');
    expect(root().querySelector('.booking-row__recurring')).toBeNull();
    expect(root().querySelector('.status-pill')).not.toBeNull();
  });

  it('offers a Cancel action for a cancellable booking and emits cancelRequested with the booking', () => {
    const booking = wireBooking({ status: 'Confirmed' });
    configure({ booking });

    let emitted: OwnBooking | undefined;
    fixture.componentInstance.cancelRequested.subscribe((b) => (emitted = b));

    const cancelButton = [...root().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Cancel') as HTMLButtonElement;
    cancelButton.click();

    expect(emitted).toEqual(booking);
  });

  it('does not offer a Cancel action for a booking that is no longer cancellable', () => {
    configure({ booking: wireBooking({ status: 'Completed' }) });

    expect([...root().querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Cancel')).toBe(false);
  });

  // Regression: a booking the background no-show job (docs/background-jobs.md, "No-show job") already
  // marked NoShow is terminal - neither action must ever be offered on it.
  it('offers neither Cancel nor Check-in for a NoShow booking', () => {
    configure({ booking: wireBooking({ status: 'NoShow' }) });

    const buttonLabels = [...root().querySelectorAll('button')].map((b) => b.textContent?.trim());
    expect(buttonLabels).not.toContain('Cancel');
    expect(buttonLabels.some((label) => label?.startsWith('Check in'))).toBe(false);
  });

  it('shows the cancellation reason for a cancelled booking', () => {
    configure({
      booking: wireBooking({ status: 'Cancelled', cancelledAtUtc: '2026-12-19T10:00:00Z', cancellationReason: 'Plans changed' }),
    });

    expect(root().textContent).toContain('Cancelled');
    expect(root().textContent).toContain('Plans changed');
  });

  // Matched by class, not label text - the label itself changes to "Checking in…" while submitting.
  function checkInButton(): HTMLButtonElement | undefined {
    return (root().querySelector('.booking-row__end .btn-primary') as HTMLButtonElement | null) ?? undefined;
  }

  describe('check-in', () => {
    afterEach(() => vi.useRealTimers());

    it('shows an enabled Check-in button for a Confirmed booking within its check-in window', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:50:00Z')); // between opens (09:45) and closes (10:15)
      configure({ booking: wireBooking() });

      const button = checkInButton();
      expect(button).toBeDefined();
      expect(button!.disabled).toBe(false);
    });

    it('disables the Check-in button before the check-in window opens', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:00:00Z')); // before opens (09:45)
      configure({ booking: wireBooking() });

      expect(checkInButton()!.disabled).toBe(true);
    });

    it('disables the Check-in button after the check-in window closes', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T10:30:00Z')); // after closes (10:15)
      configure({ booking: wireBooking() });

      expect(checkInButton()!.disabled).toBe(true);
    });

    it('does not show a Check-in button for a booking that is not Confirmed', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:50:00Z'));
      configure({ booking: wireBooking({ status: 'Pending' }) });

      expect(checkInButton()).toBeUndefined();
    });

    it('shows the checked-in time instead of a button once already checked in', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:50:00Z'));
      configure({ booking: wireBooking({ checkedInAtUtc: '2026-12-20T09:46:00Z' }) });

      expect(checkInButton()).toBeUndefined();
      expect(root().textContent).toContain('Checked in');
    });

    it('emits checkInRequested with the booking when the Check-in button is clicked', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:50:00Z'));
      const booking = wireBooking();
      configure({ booking });

      let emitted: OwnBooking | undefined;
      fixture.componentInstance.checkInRequested.subscribe((b) => (emitted = b));
      checkInButton()!.click();

      expect(emitted).toEqual(booking);
    });

    it('disables the Check-in button and shows a loading label while a check-in request is in flight', () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date('2026-12-20T09:50:00Z'));
      TestBed.configureTestingModule({ imports: [BookingRowComponent] });
      fixture = TestBed.createComponent(BookingRowComponent);
      fixture.componentRef.setInput('booking', wireBooking());
      fixture.componentRef.setInput('resourceName', 'Falcon Room');
      fixture.componentRef.setInput('checkInSubmitting', true);
      fixture.detectChanges();

      const button = checkInButton()!;
      expect(button.disabled).toBe(true);
      expect(button.textContent).toContain('Checking in');
    });
  });

  // Required scenario 5: this row must show the resource's own local time as primary, with the viewer's
  // local time as a secondary line only when the two actually differ. The Intl mock must be installed
  // BEFORE TestBed.createComponent runs (viewerZoneId is captured once, at construction) - configure()
  // above already does that unconditionally, so these live in their own describe with their own mocking.
  describe('dual timezone display', () => {
    afterEach(() => vi.restoreAllMocks());

    it("shows the resource's own local time as primary, and the viewer's local time as a secondary line when the zones differ", () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Asia/Tokyo' } as Intl.ResolvedDateTimeFormatOptions);
      configure({
        booking: wireBooking({ startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z', timeZoneId: 'Europe/Sarajevo' }),
      });

      const primary = root().querySelector('.booking-row__time')!;
      const secondary = root().querySelector('.booking-row__time-secondary')!;
      expect(primary.textContent).toContain('Jul 15');
      expect(primary.textContent).toContain('10:15');
      expect(primary.textContent).toContain('11:15');
      expect(primary.textContent).toContain('Europe/Sarajevo');
      expect(secondary.textContent).toContain('17:15');
      expect(secondary.textContent).toContain('18:15');
      expect(secondary.textContent).toContain('Asia/Tokyo');
    });

    it('shows only one time when the booking timezone matches the viewer zone', () => {
      vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Sarajevo' } as Intl.ResolvedDateTimeFormatOptions);
      configure({
        booking: wireBooking({ startUtc: '2026-07-15T08:15:00Z', endUtc: '2026-07-15T09:15:00Z', timeZoneId: 'Europe/Sarajevo' }),
      });

      expect(root().querySelector('.booking-row__time-secondary')).toBeNull();
      const primary = root().querySelector('.booking-row__time')!;
      expect(primary.textContent).toContain('10:15');
      expect(primary.textContent).toContain('11:15');
    });
  });
});
