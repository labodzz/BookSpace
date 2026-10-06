# Calendar subscription feed (WP-8)

Lets a member turn their own Confirmed bookings into a private, pollable ICS feed an external calendar
app (Google Calendar, Apple Calendar, Outlook, ...) can subscribe to - a live feed that refreshes on the
external app's own schedule, not a one-time `.ics` file download. See `BookSpace.Application/Calendar/`,
`BookSpace.Api/Controllers/CalendarSubscriptionsController.cs`, and
`BookSpace.Api/Controllers/CalendarFeedController.cs`.

## Scope

In scope: a member's own calendar subscription (create/revoke/regenerate), the anonymous ICS feed
endpoint, and a minimal My Bookings UI panel to manage it. Explicitly **out of scope** (not built here):
Gmail OAuth, two-way sync back into BookSpace, a shared tenant-wide calendar, emailing an ICS attachment,
and any new background job - the feed is generated on demand, synchronously, per request.

## Endpoints

| Method | Route | Auth | Purpose |
| --- | --- | --- | --- |
| `GET` | `/api/calendar-subscriptions` | JWT (`[Authorize]`) | Status: does the caller have an active subscription, and when was it created. Never returns the token or feed URL. |
| `POST` | `/api/calendar-subscriptions` | JWT | Create a new subscription. `409 CalendarSubscription.AlreadyExists` if one is already active - call Regenerate instead. |
| `POST` | `/api/calendar-subscriptions/regenerate` | JWT | Rotate the token: revokes the current active row and inserts a new one in the same write. `404 CalendarSubscription.NotFound` if none is active. |
| `DELETE` | `/api/calendar-subscriptions` | JWT | Revoke the active subscription. `404 CalendarSubscription.NotFound` if none is active. |
| `GET` | `/api/calendar-feed?token=...` | **None** (`[AllowAnonymous]`) - the token itself is the credential | Returns the ICS feed (`text/calendar; charset=utf-8`) for the token's owner. |

Create/Regenerate both return `{ feedUrl, createdAtUtc }`. `feedUrl` is a full, absolute URL (see
"Public base URL" below) - the raw token's one and only appearance anywhere in the system.

## Token lifecycle and security

- **Generation/storage**: `SecureTokenGenerator` (`BookSpace.Application/Auth`) - the same 512-bit
  random-value-plus-SHA-256-hash convention already used for `RefreshToken`/`Invitation`. Only
  `CalendarSubscription.TokenHash` is ever persisted; the raw token is returned to the owner exactly once,
  at creation/regeneration time, and is never logged (see "Logging" below).
- **One row per issuance, not mutated in place**: regenerating revokes the previous active row
  (`RevokedAtUtc = now`) and inserts a brand-new row with a fresh `TokenHash` - the same "reissue"
  convention `Invitation` already uses. A filtered unique index on `CalendarSubscription.UserId`
  (`WHERE RevokedAtUtc IS NULL`) guarantees at most one ACTIVE subscription per user at the database level
  - the real backstop behind a concurrent double-click on Regenerate, not merely an application-level
    precondition check.
- **Ownership**: every management command (Create/Regenerate/Revoke) takes no `UserId`/`TenantId` from the
  caller - both are always `ICurrentUserContext`-derived, the same pattern every other Create handler in
  this codebase follows. A caller can never target another user's subscription by any parameter.
- **Feed-side isolation**: the feed endpoint is anonymous, so there is no `ICurrentUserContext.TenantId`
  to drive the database's ambient tenant query filter at all. `ICalendarSubscriptionRepository.FindActiveByTokenHashAsync`
  and `IBookingRepository.GetForCalendarFeedAsync` both explicitly `IgnoreQueryFilters()` for this one
  lookup and then explicitly re-apply `TenantId`/`UserId` equality themselves - the same sanctioned
  exception shape `UserRepository.FindByEmailAsync`/`FindByIdForAuthenticationAsync` already use for
  login/refresh (see `docs/tenant-isolation.md`). Nothing about resolving the token ever widens what the
  feed itself is allowed to read.
- **Deactivation/suspension is enforced live, every request**: `GetCalendarFeedQueryHandler` re-checks
  `User.Status == Active` and `Tenant.Status == Active` on **every** feed fetch (via
  `IUserRepository.FindByIdForAuthenticationAsync`, the same tenant-filter-bypassing lookup
  `AuthenticationService.RefreshAsync` uses), not just at token-creation time - a member deactivated (or a
  tenant suspended) after issuing a subscription link loses access to the feed on their very next poll,
  exactly like they'd lose API access generally.
- **Generic failure, no state leakage**: an unknown hash, a revoked token, a deactivated user, and a
  suspended tenant all collapse into the identical `404 CalendarSubscription.NotFound` - the same
  "don't confirm account state to an outside caller" philosophy `docs/authentication.md` already documents
  for login/refresh.

## Logging and caching

- The token is passed as a **query-string parameter** (`?token=...`), never a route segment. ASP.NET
  Core's default `UseSerilogRequestLogging()` summary line (`HTTP {Method} {RequestPath} responded ...`)
  logs `RequestPath` only, never the query string - confirmed by reading
  `Serilog.AspNetCore`'s default template and by grepping this codebase's own exception handlers
  (`NotFoundExceptionHandler`/`ConflictExceptionHandler`/`GlobalExceptionHandler`/`ValidationExceptionHandler`),
  all of which likewise log only `httpContext.Request.Path`. Putting the token in a route segment instead
  would have put it directly into that log line; the query string does not.
- `GetCalendarFeedQueryHandler`'s `NotFoundException` message is a fixed string ("Calendar subscription
  not found.") - it never interpolates the token, so even the one place this exception's message IS
  logged (`NotFoundExceptionHandler`, at `Information` level) never contains it either.
- **Known limitation**: this only covers logging inside this application. A reverse proxy, CDN, or APM
  layer in front of it that independently logs full request URLs (including query strings) could still
  capture the token - an operator deploying this feature should verify their own edge/proxy log
  configuration doesn't do that for this route, or add query-string scrubbing there.
- `CalendarFeedController.GetFeed` is annotated `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`,
  emitting `Cache-Control: no-store` - this sensitive, per-user URL's response is never eligible for
  browser or shared/intermediate caching.

## Booking status -> feed mapping

| `BookingStatus` | In the feed? | Why |
| --- | --- | --- |
| `Confirmed` | Yes (`STATUS:CONFIRMED`) | The feed's whole basis - an actual reservation. |
| `Completed` | Yes (`STATUS:CONFIRMED`) | Domain-equivalent to Confirmed (a Confirmed booking whose time has passed) - no handler in this codebase currently ever sets this status, but the feed treats it identically to Confirmed for forward-compatibility rather than silently dropping it if something starts setting it later. |
| `Pending` | No | Not yet a real reservation - showing it as an event would misrepresent an undecided request as confirmed. |
| `Rejected` | No | Never became a real reservation. |
| `Expired` | No | The approval workflow's own timeout outcome - also never became a real reservation. |
| `Cancelled` | No | Must not remain active in the external calendar - see "Cancellation/no-show approach" below. |
| `NoShow` | No | No longer an active confirmed reservation. |

`IBookingRepository.GetForCalendarFeedAsync` filters to exactly `Confirmed`/`Completed` in the database
query itself (`FeedActiveStatuses.Contains(booking.Status)`) - no other status is ever materialized into
the feed-building step at all, so there is no risk of a UI/formatting layer accidentally re-including one.

### Cancellation/no-show approach (and its limitation)

This feed **omits** Cancelled/NoShow/Rejected/Expired/Pending bookings entirely, rather than emitting a
`STATUS:CANCELLED` placeholder `VEVENT` for them. This matches how major calendar providers actually
behave for a subscribed/webcal calendar: on each refetch they reconcile their local copy against the
fetched `VCALENDAR` by UID, and a UID that's no longer present is removed. Omission is also simpler (one
fewer code path to get subtly wrong), avoids a second "is `STATUS:CANCELLED` well-supported everywhere"
compatibility question, and never discloses "there used to be a booking here" to the external calendar
provider.

**Limitation this doesn't hide**: a subscription's refresh interval is entirely up to the external
calendar app (commonly every few hours, sometimes longer) - this feed cannot push updates or force an
immediate refresh. A booking cancelled five minutes ago may still show as an upcoming event in someone's
external calendar until that app's own next poll. This is disclosed here and in the UI copy rather than
implied away.

## Stable UID, DTSTAMP, and no RRULE expansion

- **UID** is `{Booking.Id:D}@bookspace` - stable for the life of that booking row, identical on every
  single fetch. This is what lets an external calendar recognize "this is the same event" across polls
  instead of creating a duplicate each time.
- **DTSTAMP** is the feed's own generation time (`DateTimeOffset.UtcNow` at request time), recomputed on
  every fetch - per RFC 5545 §3.8.7.2 this reflects when the representation was produced, not the event's
  own identity, so it is expected to differ between two fetches even when nothing about the booking changed.
- Every recurring occurrence already exists as its own independent `Booking` row (see
  `docs/recurring-bookings-and-approvals.md`) - the feed emits one plain `VEVENT` per occurrence/row,
  with **no `RRULE`** and no client-side recurrence expansion of its own. This avoids re-deriving a
  recurrence rule from already-materialized rows (a lossy, error-prone translation) and keeps each
  occurrence's own lifecycle (an individually cancelled occurrence, say) naturally independent in the feed.

## UTC instants

`DTSTART`/`DTEND` are always written as `yyyyMMdd'T'HHmmss'Z'` against the booking's real UTC instant (RFC
5545 §3.3.5, form #2: "date with UTC time") - never a local/floating time, never a `TZID` parameter. Every
calendar client converts a `Z`-suffixed instant to its own display zone itself; because the UTC instant
never changes across a DST transition, this sidesteps DST entirely rather than needing its own DST policy
the way `docs/availability-and-timezones.md`'s *local*-time conversions do. The resource's own IANA
timezone is deliberately **not** additionally embedded in the event text - the single authoritative UTC
instant is sufficient, and a second, textual "resource-local time" would only ever be redundant or (if a
conversion bug crept in) actively contradictory.

## RFC 5545 compliance

`IcsCalendarWriter` (`BookSpace.Application/Calendar/IcsCalendarWriter.cs`) is a small, hand-rolled writer
scoped to exactly this feed's fixed property set (`VCALENDAR`/`VEVENT`, `VERSION`, `PRODID`, `UID`,
`DTSTAMP`, `DTSTART`, `DTEND`, `SUMMARY`, `DESCRIPTION`, `STATUS`) - no recurrence rules, no timezones
beyond UTC, no attendees/organizers/alarms, so a full third-party iCalendar library would bring far more
surface area than this ever exercises. It implements, and `IcsCalendarWriterTests` verifies against an
**independent** parser (`Ical.Net`, a test-only dependency of `BookSpace.Application.Tests` - never
referenced by any shipped project):

- RFC 5545 §3.1 line folding at 75 **octets** (not characters), splitting only on a UTF-8 character
  boundary - proven with a resource name long enough to force folding, entirely composed of multi-byte
  Cyrillic characters, round-tripped byte-for-byte through folding and back.
- CRLF line termination throughout.
- RFC 5545 §3.3.11 TEXT escaping (`\`, `;`, `,`, and newlines) applied to every piece of user-controlled
  text (`Resource.Name`) before it is ever written - proven with a resource name containing a raw,
  unescaped `\nEND:VEVENT\nBEGIN:VEVENT\n...` payload, which round-trips back out as plain text, never as
  an injected second component. This is the actual ICS-injection defense: user text can never contain an
  unescaped CRLF in the output, so it can never end a content line and start a bogus new property/component.
- Unicode resource names (Cyrillic, emoji, accents) round-trip exactly.
- A feed with zero eligible bookings is still a structurally valid, parseable `VCALENDAR` (zero `VEVENT`s
  is valid per the spec).

## Query and scaling

- `IBookingRepository.GetForCalendarFeedAsync` filters by `TenantId`, `UserId`, status, and the time
  window **in the database query itself** (a single query, joined directly against `Resources` for the
  name) - it never starts from the paginated `GetOwnBookingsAsync` shape (whose default page size is 20)
  and never materializes a user's full booking history into memory before filtering. Proven in
  `BookingRepositoryCalendarFeedTests.GetForCalendarFeedAsync_ReturnsEveryBooking_EvenFarMoreThanOnePageWorthOfTheOrdinaryEndpoint`
  (35 seeded bookings, all 35 returned).
- **No separate event-count cap.** The only size bound is the time window below - deliberately, so the
  feed is never silently truncated and presented as if it were complete. A personal subscription feed
  realistically never accumulates enough Confirmed bookings inside a bounded window to make this a
  concern.

### Time window

`CalendarFeedOptions.PastWindowDays` (default 30) / `FutureWindowDays` (default 365), validated at startup
(`CalendarFeedOptionsValidator`, `IValidateOptions<CalendarFeedOptions>`, the same `ValidateOnStart()`
convention as `BackgroundJobsOptions`/`EmailOptions`). The window is `[now - PastWindowDays, now +
FutureWindowDays)`, recomputed from the real current instant on every single feed request. The past side
exists so a calendar app that hasn't refreshed in a while still sees recently-past bookings it may already
be displaying; the future side bounds how far ahead the query (and therefore the feed) ever reaches.
Configurable per deployment via the `CalendarFeed:PastWindowDays`/`CalendarFeed:FutureWindowDays`
configuration keys, bounded by `CalendarFeedOptions.MaxPastWindowDays`/`MaxFutureWindowDays` (365/730 days)
so a misconfigured value can't make the query unboundedly wide.

## Public base URL (`CalendarFeed:PublicBaseUrl`)

The absolute feed URL returned by Create/Regenerate is built from a required, explicitly-configured
`CalendarFeed:PublicBaseUrl` setting (`CalendarFeedUrlBuilder.Build`), **never** from the incoming
request's `Host`/`Scheme` - a `Host` header is caller-controlled and untrustworthy as the source of a URL
this app hands back to a client (the classic Host-header-injection concern). This mirrors the existing
`Cors:AllowedOrigins` convention (`docs/container-deployment.md`, "Reverse proxy and Azure ingress"):
explicitly configured per deployment, not inferred from the request.

- `CalendarFeedOptionsValidator` fails startup (`ValidateOnStart()`) if `PublicBaseUrl` is missing or not
  an absolute `http`/`https` URL.
- Configured in `appsettings.Development.json` as `http://localhost:5185` (matching the dev API's own
  port - see `environment.development.ts`'s `apiUrl`).
- Production: set `CalendarFeed__PublicBaseUrl` to the deployment's real externally-reachable origin (see
  `docs/container-deployment.md`'s Azure environment variable list) - the same FQDN already used for
  `Cors__AllowedOrigins__0`.
- Not present in the base `appsettings.json` (same treatment as `Auth`'s required-but-secret settings) -
  an unconfigured non-Development deployment fails fast at startup rather than silently building a broken
  URL.
- **Local Podman/Compose** (`compose.local.yaml`): `CalendarFeed__PublicBaseUrl` is set to
  `http://localhost:8080`, matching the host port this container is actually published on (the `ports`
  mapping just below it in the same file) - **found missing and fixed during the WP-8 final audit** (see
  that audit's own report): without it, the containerized app would either fail `ValidateOnStart()` outright
  (if `appsettings.Development.json`'s own value somehow weren't picked up) or, as actually would have
  happened (that file's `CalendarFeed:PublicBaseUrl=http://localhost:5185` *is* published into the
  container image and *is* read there, since `ASPNETCORE_ENVIRONMENT=Development` in this compose file),
  silently build every subscription link against port 5185 - nothing this container ever listens on - so
  every copied link would have been dead on arrival. `http://localhost:8080` only works from a browser/
  calendar app on the SAME machine running `podman compose` - this is explicitly a **local-only** value,
  never reachable by an external/cloud calendar provider (Google Calendar, Outlook.com, ...), which can
  never resolve "localhost" to mean this machine. A real deployment needs this set to that deployment's own
  actual externally-reachable HTTPS origin instead (see the Production bullet above).

## Database

New entity `CalendarSubscription` (`BookSpace.Domain/Entities/CalendarSubscription.cs`), migration
`20261006094340_AddCalendarSubscriptions`. Table `CalendarSubscriptions`: `Id`, `TenantId`, `UserId`,
`TokenHash` (unique), `CreatedAtUtc`, `RevokedAtUtc` (nullable). Indexes: unique on `TokenHash`, plain on
`TenantId`, and a filtered unique index on `UserId WHERE RevokedAtUtc IS NULL` (at most one active
subscription per user). No `RowVersion` column - see the entity's own doc comment for why the filtered
unique index alone is sufficient here, unlike `RefreshToken`/`Invitation`.

## Frontend

`CalendarSubscriptionPanelComponent` (`frontend/src/app/features/bookings/calendar-subscription-panel/`),
embedded in My Bookings (`my-bookings.html`, directly under the page header). States: loading, load error
(retry), no subscription (Create), just-created/regenerated (shows the link once, with a copy button and
an explicit "won't be shown again" warning), existing-but-hidden (status only, Regenerate/Revoke), and a
revoke confirmation step. The feed URL is held only in an in-memory signal - never written to
`localStorage`/`sessionStorage`, never logged to the console. Copy-to-clipboard follows the same
try/catch-with-fallback-message pattern already used by the diagnostics panel
(`core/diagnostics/diagnostics.service.ts`).

## Testing

- **`BookSpace.Application.Tests/Calendar/`**: handler unit tests (Create/Regenerate/Revoke/Status/Feed -
  success, conflict, not-found, deactivated-user, suspended-tenant, window-derivation),
  `CalendarFeedOptionsValidatorTests`, and `IcsCalendarWriterTests` (RFC 5545 compliance, verified against
  `Ical.Net` - see "RFC 5545 compliance" above).
- **`BookSpace.Infrastructure.Tests/Persistence/`**: `CalendarSubscriptionRepositoryTests` (real LocalDB -
  ambient-vs-bypassed tenant filter, the filtered-unique-index race backstop) and
  `BookingRepositoryCalendarFeedTests` (real LocalDB - status filtering, tenant/user isolation, resource
  join, window boundaries, more-than-one-page result set, ordering).
- **`BookSpace.Api.Tests/Calendar/`**: `CalendarSubscriptionsEndpointsTests` (full HTTP, authenticated
  management flow, old-token-after-regenerate) and `CalendarFeedEndpointsTests` (full HTTP, genuinely
  anonymous access, content-type, `Cache-Control: no-store`, cross-tenant/cross-user isolation, status
  mapping, recurring-series occurrences, UTC instants, stable UID across repeated fetches, empty-but-valid
  feed, Unicode resource name).
- Frontend: `calendar-subscription.service.spec.ts` and `calendar-subscription-panel.spec.ts` (create/
  copy/regenerate/revoke-with-confirmation flows, loading/error states).

### Results (this task)

- `BookSpace.Application.Tests`: 709/709 passed.
- `BookSpace.Infrastructure.Tests`: 332/332 passed (real LocalDB).
- `BookSpace.Api.Tests`: 300/300 passed.
- Backend `dotnet build`/`dotnet publish` (Release): succeeded, 0 warnings, 0 errors.
- Frontend `ng test` (full suite): 513/513 passed, 42/42 files.
- Frontend `ng build --configuration production`: succeeded (pre-existing, unrelated `dashboard.scss`
  budget warning only).

A frontend test run initially showed 3 unrelated spec files (`resource-detail`, `resource-list`,
`auth.guard`) failing only when run as part of the full suite, never in isolation. Root-caused (not
dismissed) to a leaked `setTimeout` in `CalendarSubscriptionPanelComponent.copyFeedUrl()` that fired
against a destroyed component's signal during a later, unrelated spec file's run - fixed by clearing it
via `DestroyRef.onDestroy()`, a genuine component-cleanup bug, not merely a test artifact. Confirmed fixed
by re-running the full suite (513/513 passed) afterward.

## Manual end-to-end verification

Automated tests above exercise every endpoint's logic against a real database (Infrastructure.Tests) or a
real in-process HTTP pipeline (Api.Tests) - they do **not** prove an actual external calendar client
(Google Calendar, Apple Calendar, Outlook) can successfully subscribe to and poll a BookSpace deployment,
since that requires a publicly reachable HTTPS URL this environment does not have. The following steps
were **not** performed in this task and are left for a real deployment:

1. Deploy with a real `CalendarFeed:PublicBaseUrl` and a publicly reachable HTTPS origin.
2. From My Bookings, create a subscription and copy the link.
3. Add it to an external calendar app as a URL subscription (not a one-time import) and confirm existing
   Confirmed bookings appear with the correct local time.
4. Create a new booking in BookSpace, wait for the external app's own next refresh cycle, and confirm it
   appears without needing to re-add the subscription.
5. Cancel a booking, wait for the external app's next refresh, and confirm it disappears.
6. Regenerate the link and confirm the old URL stops resolving while the external app (still pointed at
   the old URL) starts failing to refresh.

What IS verified without that deployment: the ICS content's RFC 5545 structure (via an independent
parser), the full HTTP contract (status codes, content type, caching header, anonymous access), and every
isolation/lifecycle rule - everything except an actual third-party calendar client's own subscribe/refresh
behavior against a public internet-reachable URL.

## Remaining limitations

- External calendar apps control their own refresh cadence - BookSpace cannot push updates or force an
  immediate refresh (see "Cancellation/no-show approach" above).
- A reverse proxy/CDN/APM layer in front of this app could independently log the token in a full request
  URL - verify that layer's own logging configuration for this route in any real deployment (see "Logging
  and caching" above).
- `Completed` is mapped identically to `Confirmed` for forward-compatibility, but no handler in this
  codebase currently ever produces that status.

## WP-8 final audit (findings)

A dedicated audit pass across the whole WP-8 surface (background jobs, lease/concurrency, outbox/email
reliability, check-in/no-show, and this feed) re-verified every guarantee documented above against the
current code and its full test suite (`BookSpace.Application.Tests` 709/709,
`BookSpace.Infrastructure.Tests` 332/332 against real LocalDB, `BookSpace.Api.Tests` 300/300, frontend
513/513, backend Release build/publish, Angular production build - all clean). One real, concrete gap was
found and fixed in this feed's own area:

- **(A - blocking, fixed)** `CalendarFeed:PublicBaseUrl` was missing from `compose.local.yaml` - see
  "Local Podman/Compose" above for the exact failure mode this would have caused and the fix applied.

No other bug was found in this feed's token lifecycle, isolation, ICS generation, or caching behavior -
every claim in this document was re-checked against the actual current code (not re-stated from memory)
and found to still hold.

**(C - pending a public HTTPS deployment, not newly discovered by this audit)**: the "Manual end-to-end
verification" section above already disclosed this feed has never been exercised against a real external
calendar client over the public internet; the final audit did not change that - there was no public HTTPS
endpoint available to test against in this environment either.
