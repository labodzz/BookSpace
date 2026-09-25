namespace BookSpace.Application.Common;

// Stable, dotted-notation ErrorCode strings attached to NotFoundException/ConflictException (or, for
// Auth - which never throws for an ordinary "wrong password"/"bad token" outcome - to the plain
// LoginResponse/RefreshResponse result record instead) so a client can branch on a machine-readable
// value instead of parsing the human-readable message. Users' handlers predate this convention and
// still throw without an ErrorCode in places, which is fine since the field is optional.
internal static class ErrorCodes
{
    public const string AuthInvalidCredentials = "Auth.InvalidCredentials";
    public const string AuthInvalidRefreshToken = "Auth.InvalidRefreshToken";
    public const string AuthRefreshReuseDetected = "Auth.RefreshReuseDetected";

    public const string ResourceNotFound = "Resource.NotFound";
    public const string ResourceTypeNotFound = "Resource.ResourceTypeNotFound";
    public const string ResourceNameConflict = "Resource.NameConflict";
    public const string ResourceAvailabilityRuleRequired = "Resource.AvailabilityRuleRequired";

    public const string AvailabilityRuleNotFound = "AvailabilityRule.NotFound";
    public const string AvailabilityRuleConflict = "AvailabilityRule.Conflict";

    public const string BlackoutPeriodNotFound = "BlackoutPeriod.NotFound";

    public const string UserNotFound = "User.NotFound";

    public const string ResourceApproverNotFound = "ResourceApprover.NotFound";
    public const string ResourceApproverConflict = "ResourceApprover.Conflict";
    public const string ResourceApproverRoleRequired = "ResourceApprover.RoleRequired";
    public const string ResourceApproverLastRemaining = "ResourceApprover.LastRemaining";

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

    // User lifecycle and role administration (Batch 4A). See docs/user-administration.md.
    public const string UserEmailConflict = "User.EmailConflict";
    public const string UserStatusConflict = "User.StatusConflict";
    public const string UserSelfLockout = "User.SelfLockout";
    public const string UserLastAdminRemaining = "User.LastAdminRemaining";
    public const string UserRoleDelegationNotAllowed = "User.RoleDelegationNotAllowed";
    public const string UserRoleConflict = "User.RoleConflict";
    public const string UserRoleNotAssigned = "User.RoleNotAssigned";
    public const string UserApproverAssignmentsExist = "User.ApproverAssignmentsExist";
    public const string RoleNotFound = "Role.NotFound";
    public const string InvitationInvalid = "Invitation.Invalid";
}
