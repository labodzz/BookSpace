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

  it('shows the cancellation reason for a cancelled booking', () => {
    configure({
      booking: wireBooking({ status: 'Cancelled', cancelledAtUtc: '2026-12-19T10:00:00Z', cancellationReason: 'Plans changed' }),
    });

    expect(root().textContent).toContain('Cancelled');
    expect(root().textContent).toContain('Plans changed');
  });
});
