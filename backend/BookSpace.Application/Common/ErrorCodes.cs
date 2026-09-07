namespace BookSpace.Application.Common;

// Stable, dotted-notation ErrorCode strings attached to NotFoundException/ConflictException so a
// client can branch on a machine-readable value instead of parsing the human-readable message.
// Only used from Resources feature code onward - earlier handlers (Auth/Users) predate this and
// keep throwing without an ErrorCode, which is fine since the field is optional.
internal static class ErrorCodes
{
    public const string ResourceNotFound = "Resource.NotFound";
    public const string ResourceTypeNotFound = "Resource.ResourceTypeNotFound";
    public const string ResourceNameConflict = "Resource.NameConflict";

    public const string AvailabilityRuleNotFound = "AvailabilityRule.NotFound";
    public const string AvailabilityRuleConflict = "AvailabilityRule.Conflict";

    public const string BlackoutPeriodNotFound = "BlackoutPeriod.NotFound";

    public const string UserNotFound = "User.NotFound";

    public const string ResourceApproverNotFound = "ResourceApprover.NotFound";
    public const string ResourceApproverConflict = "ResourceApprover.Conflict";

    public const string ResourceTypeNameConflict = "ResourceType.NameConflict";
    public const string ResourceTypeInUse = "ResourceType.InUse";

    public const string BookingNotFound = "Booking.NotFound";
    public const string BookingResourceUnavailable = "Booking.ResourceUnavailable";
    public const string BookingOutsideAvailability = "Booking.OutsideAvailability";
    public const string BookingBlackoutConflict = "Booking.BlackoutConflict";
    public const string BookingCapacityExceeded = "Booking.CapacityExceeded";
    public const string BookingCancellationNotAllowed = "Booking.CancellationNotAllowed";
    public const string BookingNoApproverConfigured = "Booking.NoApproverConfigured";
    public const string BookingApprovalNotAllowed = "Booking.ApprovalNotAllowed";
    public const string BookingApprovalForbidden = "Booking.ApprovalForbidden";

    public const string RecurringSeriesNotFound = "RecurringSeries.NotFound";
    public const string RecurringSeriesNoValidOccurrences = "RecurringSeries.NoValidOccurrences";
}
