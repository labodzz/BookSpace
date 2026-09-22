namespace BookSpace.Domain.Enums;

// Active is deliberately the first (0/default) member, not Invited, even though Invited reads as the
// more "natural" starting state - EF Core treats a property's CLR default value as the sentinel meaning
// "not set, use the database DEFAULT constraint" for any property configured with HasDefaultValue (see
// BookSpaceModelConfiguration's User.Status). If Invited were 0, InviteUserCommandHandler explicitly
// setting Status = UserStatus.Invited would look identical to "never set" to EF, and it would silently
// omit the column from the INSERT - letting the DB default (Active) apply instead, defeating the entire
// invitation flow (a newly invited user would come back Active, with an empty password hash, able to
// silently bypass ever accepting the invitation). Confirmed live via `dotnet ef migrations add`'s own
// sentinel warning before this fix.
//
// Active: normal, can log in. Invited: a User row exists (so its globally-unique Email is already
// reserved and roles can already be staged via UserRole) but no password has been set yet - cannot log
// in. Inactive: administratively deactivated - cannot log in, but the row and its history are kept
// (never physically deleted). See docs/user-administration.md for the full lifecycle and the
// transitions each admin action allows.
public enum UserStatus
{
    Active,
    Invited,
    Inactive,
}
