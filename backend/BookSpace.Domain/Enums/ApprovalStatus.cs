namespace BookSpace.Domain.Enums;

// Expired (added by the stale-approval job - see docs/background-jobs.md, "Stale-approval job") is a
// terminal outcome distinct from Rejected on purpose: Rejected means an approver made a decision;
// Expired means nobody did, within the window ApprovalRequest.ExpiresAtUtc already defines. Collapsing
// the two into "Rejected" (with a reason) would hide, from anyone reading the row, whether a human ever
// looked at this request at all - exactly the distinction the stale-approval job must never erase.
// Stored as a string column (see BookSpaceModelConfiguration), so this needed no migration to introduce.
public enum ApprovalStatus { Pending, Approved, Rejected, Cancelled, Expired }
