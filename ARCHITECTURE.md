# BookSpace architecture

## Dependency rule

`BookSpace.Domain` has no project dependencies.
`BookSpace.Application` depends only on Domain.
`BookSpace.Infrastructure` implements Application contracts.
`BookSpace.Api` and `BookSpace.Worker` are composition roots.

## Critical implementation rules

1. Every tenant-owned row has `TenantId`; database-level tenant isolation is required, not only EF query filters.
2. Booking confirmation and approval re-check happen in one transaction; the database is the final authority for overlap and capacity protection.
3. Store instants in UTC; interpret availability and recurrence in the resource timezone and document the DST policy.
4. Persist notifications in an outbox transactionally and use idempotency keys in jobs.
5. AI only proposes a booking; it must call the same application booking path as the UI.

## Resolve before implementation

- Recurring occurrences affected by a new blackout.
- TenantAdmin cancellation of another member's booking.
- UI timezone display rule.
