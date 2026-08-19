# Test strategy

## Unit tests

Unit tests belong next to the layer whose logic they exercise. They do not use a
database, HTTP server, clock or email provider. Focus first on booking-rule,
recurrence, timezone/DST, capacity and approval decision behaviour.

## Integration tests

`BookSpace.IntegrationTests` hosts the API through `WebApplicationFactory`.
Once persistence is added, its factory must use an isolated test database and
apply the same tenant-isolation policy and migrations as production.

Prioritise the PRD acceptance criteria:

1. Cross-tenant identifiers never return data from another tenant.
2. Two simultaneous requests for a final slot produce exactly one confirmation.
