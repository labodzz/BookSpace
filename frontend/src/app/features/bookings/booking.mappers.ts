import { BookingStatus, CancelBookingResponse, CreateBookingResponse, CreateRecurringSeriesResponse, OwnBooking, RecurrenceFrequency, RecurringSeriesOccurrence } from './booking.models';

// Confirmed live against the running API: BookSpace serializes every enum in a JSON body as its raw
// underlying number (System.Text.Json's default - no JsonStringEnumConverter is registered anywhere in
// the pipeline). This array is the wire contract: index = the enum's underlying int, matching
// BookSpace.Domain.Enums.BookingStatus exactly (Pending, Confirmed, Rejected, Cancelled, Completed,
// NoShow). Every booking-shaped response is mapped through toBookingStatus at the service boundary so
// the rest of the app can keep working with readable string statuses instead of magic numbers.
const BOOKING_STATUS_BY_CODE: readonly BookingStatus[] = ['Pending', 'Confirmed', 'Rejected', 'Cancelled', 'Completed', 'NoShow'];

// The inverse mapping, for the one JSON-body enum the frontend SENDS: CreateRecurringSeriesRequest.Frequency.
// Yearly (3) is deliberately absent - the API's own validator rejects it, and the recurring booking form
// never offers it.
const RECURRENCE_FREQUENCY_CODE: Readonly<Record<RecurrenceFrequency, number>> = { Daily: 0, Weekly: 1, Monthly: 2 };

export function toBookingStatus(code: number): BookingStatus {
  return BOOKING_STATUS_BY_CODE[code] ?? 'Pending';
}

export function toRecurrenceFrequencyCode(frequency: RecurrenceFrequency): number {
  return RECURRENCE_FREQUENCY_CODE[frequency];
}

export interface OwnBookingWire extends Omit<OwnBooking, 'status'> {
  status: number;
}

export function mapOwnBooking(wire: OwnBookingWire): OwnBooking {
  return { ...wire, status: toBookingStatus(wire.status) };
}

export interface CreateBookingResponseWire extends Omit<CreateBookingResponse, 'status'> {
  status: number;
}

export function mapCreateBookingResponse(wire: CreateBookingResponseWire): CreateBookingResponse {
  return { ...wire, status: toBookingStatus(wire.status) };
}

export interface CancelBookingResponseWire extends Omit<CancelBookingResponse, 'status'> {
  status: number;
}

export function mapCancelBookingResponse(wire: CancelBookingResponseWire): CancelBookingResponse {
  return { ...wire, status: toBookingStatus(wire.status) };
}

export interface RecurringSeriesOccurrenceWire extends Omit<RecurringSeriesOccurrence, 'status'> {
  status: number;
}

export interface CreateRecurringSeriesResponseWire extends Omit<CreateRecurringSeriesResponse, 'createdOccurrences'> {
  createdOccurrences: RecurringSeriesOccurrenceWire[];
}

export function mapCreateRecurringSeriesResponse(wire: CreateRecurringSeriesResponseWire): CreateRecurringSeriesResponse {
  return {
    ...wire,
    createdOccurrences: wire.createdOccurrences.map((occurrence) => ({ ...occurrence, status: toBookingStatus(occurrence.status) })),
  };
}
