# BookSpace Documentation

Living documentation of the current system, written from the actual implementation - not from
assumptions and not merely from audit findings. Start here, then follow links into the topic you need.

- **[architecture.md](architecture.md)** - layers, request flow, current status of every major area
  (implemented / deferred / not yet built). Start here if you're new to the codebase.
- **[tenant-isolation.md](tenant-isolation.md)** - the fail-closed tenant query filter, the two
  sanctioned unscoped-lookup exceptions, and a checklist for adding a new tenant-owned relationship.
- **[authentication.md](authentication.md)** - the refresh token model, rotation, reuse detection, and
  why an actual-reuse response differs from a concurrent-race response.
- **[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md)** - the full
  `ResourceStatus` behavior matrix and the capacity-reduction invariant/sweep-line.
- **[availability-and-timezones.md](availability-and-timezones.md)** - wall-clock local rules vs. UTC
  storage, the DST spring-forward/fall-back/transition-crossing policy.
- **[optimistic-concurrency.md](optimistic-concurrency.md)** - the shared `RowVersion` mechanism behind
  both the auth and resource concurrency guards, and guidance for adding it to a future entity.
- **[bookings-and-concurrency.md](bookings-and-concurrency.md)** - booking lifecycle, ownership,
  rejection-reason contract, capacity/interval semantics, cancellation semantics, and the
  transaction-scoped resource-row lock that makes double-booking impossible under real concurrency -
  with the SQL Server/LocalDB test evidence proving it.
- **[recurring-bookings-and-approvals.md](recurring-bookings-and-approvals.md)** - recurrence storage/
  generation model, per-occurrence conflict surfacing, the approval workflow and its own concurrency
  boundary, why recurrence's DST policy deliberately differs from the availability query's, and a real
  EF Core identity-map concurrency bug this work found and fixed.
- **[audit-remediation-summary.md](audit-remediation-summary.md)** - what was fixed in the
  2026-09-04 remediation pass, what was deferred and why, classified by risk type.
- **[open-questions.md](open-questions.md)** - genuinely unresolved decisions, each with its context,
  options, and the trigger that should force an answer. Do not treat anything here as settled.
- **[next-work-packet-handoff.md](next-work-packet-handoff.md)** - what the next Work Packet needs to
  check against before implementation starts.
- **[container-deployment.md](container-deployment.md)** - the multi-stage Dockerfile, local Podman
  Compose setup, production migrations/bootstrap, and the Azure Container Apps environment variables/
  secrets needed to deploy.

See [AI-USAGE.md](../AI-USAGE.md) for the transparency log of how AI assistance was used while building
this project, and the top-level [README.md](../README.md) for setup/running instructions.
