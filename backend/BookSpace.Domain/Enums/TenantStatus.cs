namespace BookSpace.Domain.Enums;

// Active -> Suspended -> Active is reversible (SysAdmin-triggered, see TenantsController). Active/
// Suspended -> Deleted is the soft-delete lifecycle and is NOT reversible through this API (see
// docs/tenant-onboarding.md, "Tenant lifecycle"). Deliberately the smallest equivalent of the task's
// suggested Active/Suspended/DeletionPending/Deleted lifecycle: a separate DeletionPending staging state
// would exist only to gate against accidental deletion, and that gate is already provided by the
// delete endpoint's own exact-name-confirmation requirement (TenantsController.DeleteTenant) - adding an
// unused intermediate status the UI never actually shows or waits in would be state with no behavior
// behind it. Suspended and Deleted both block login/refresh/new bookings/resource changes and are
// skipped by background jobs, but are kept as distinct states so a SysAdmin can always tell "paused for
// cause, expected to come back" apart from "on its way out." Neither status implies the underlying data
// was ever physically deleted - see Tenant.cs.
public enum TenantStatus { Active, Suspended, Deleted }
