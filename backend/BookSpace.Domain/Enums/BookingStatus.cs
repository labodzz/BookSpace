namespace BookSpace.Domain.Enums;

// Expired (added by the stale-approval job - see docs/background-jobs.md, "Stale-approval job") is the
// terminal state a booking reaches when its ApprovalRequest expired without a decision - deliberately
// NOT Rejected: Rejected is a human approver's decision, Expired is the system's, and conflating the two
// would silently misrepresent an automatic timeout as someone having looked at and declined the request.
// Like every other BookingStatus, this frees the resource's capacity the moment it's set (see
// BookingAvailabilityRepository.ActiveStatuses, which - unchanged by this - only ever counts Pending and
// Confirmed). Stored as a string column, so this needed no migration to introduce by itself.
public enum BookingStatus { Pending, Confirmed, Rejected, Cancelled, Completed, NoShow, Expired }
