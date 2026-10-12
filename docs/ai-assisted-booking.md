# AI-Assisted Booking (WP-9)

Natural-language booking: an authenticated tenant member types a request in Bosnian or English (e.g.
"Treba mi laboratorija za 3 osobe sljedeći utorak od 14 do 16." / "Book a lab for 3 people next Tuesday
from 2 to 4 PM"), the backend calls an LLM to turn it into structured intent, resolves a real candidate
against this tenant's own resources and the existing booking engine, and the user explicitly reviews and
confirms it before anything is actually booked.

**The existing booking engine remains the sole authority.** This feature never creates a `Booking`,
`ApprovalRequest`, or `NotificationOutboxItem` itself, and never acquires `IResourceBookingLock` - see
"Architecture" below for exactly how that's structurally guaranteed, not just intended.

## User flow

1. The user opens the floating assistant (bottom-right, on every authenticated tenant page - see "Floating
   assistant" below) and types a request, or a reply to a clarification question.
2. The frontend calls `POST /api/ai-bookings/propose` with the text and a short, client-held conversation
   transcript (see "Clarification and conversation state" below).
3. The backend (`ProposeBookingCommandHandler`) calls the configured LLM provider
   (`IBookingIntentInterpreter`) to interpret the text into structured fields, then independently resolves
   and validates everything - see "Resolution pipeline" below.
4. The response is one of five outcomes: `Proposed` (a concrete, eligible candidate), `NeedsClarification`
   (a specific follow-up question, in the user's own language), `NoSuitableResource` (nothing matches, or
   the match isn't actually available), `Disabled` (the feature isn't turned on), or `ProviderUnavailable`
   (the LLM call failed/timed out).
5. On `Proposed`, the assistant shows a structured proposal card within the conversation (resource, start,
   end, time zone, quantity, whether it requires approval) and asks for explicit confirmation - never
   presented as an already-made reservation, and the conversation itself keeps flowing around it (the user
   can keep typing - see "Floating assistant" below for how this differs from the original one-shot form).
6. Confirming sends `{resourceId, startUtc, endUtc, quantity}` to the **existing**
   `POST /api/bookings` (`CreateBookingCommandRequest`/`CreateBookingCommandHandler`) - the exact same
   endpoint `BookingFormComponent`'s manual flow already uses. This re-runs the real eligibility check,
   the real `IResourceBookingLock`, and the real approval-workflow branch from scratch; a proposal is a
   snapshot, never a guarantee (see `docs/bookings-and-concurrency.md`, "read model, not a guarantee" -
   the identical caveat this task's own proposal step inherits).

## Floating assistant

A floating launcher (bottom-right, `AiAssistantWidgetComponent`) is rendered once, by `ShellComponent` -
the layout every authenticated tenant page already renders inside of - so it is reachable from every
tenant page, persists across normal in-app navigation (the shell itself is never destroyed by a child-route
change, only its `<router-outlet>` content swaps), and never exists as more than one instance. It is hidden
for the global SysAdmin (`shell.html`'s `@if (!isSysAdmin)`, the same gating already used for every other
tenant-business nav item) - `POST /api/ai-bookings/propose` rejects that role server-side
(`TenantMembershipGuard`) exactly like `POST /api/bookings` itself does, so the launcher is never offered
to a role that could never actually use it. It is structurally absent (not merely hidden) on `welcome`,
`login`, and `accept-invitation`, which render outside `ShellComponent` entirely (see `app.routes.ts`).

**State lives in `AiAssistantService`** (`providedIn: 'root'`), not in the panel component: the panel is
only mounted while open (a plain `@if` in `AiAssistantWidgetComponent`), but the conversation - and,
critically, an in-flight `propose()`/confirm `POST /api/bookings` call - must survive the panel being
closed. Every HTTP subscription is therefore rooted on the service's own (whole-app-session) lifetime, not
a component's `DestroyRef`:

- **Open/close** (`toggleOpen`/`open`/`close`) only flips a `isOpen` signal - the conversation itself is
  untouched, so reopening shows exactly what was there before. Closing mid-confirmation leaves that
  `POST /api/bookings` call running; its result (confirmed/pending/failed) is applied to the service's
  state regardless, and is simply waiting to be seen again next time the panel opens.
- **"New conversation"** (`resetConversation`) is the only thing that clears the transcript - it also bumps
  the internal request-sequence counter, so a `propose()`/confirm response that was already in flight for
  the OLD conversation can never repopulate the new one when it eventually arrives (the same stale-response
  guard the original one-shot form used, generalized to cover a reset mid-flight, not just a newer `send()`
  superseding an older one).
- **Logout or a different login (identity/tenant change)** clears everything automatically, via a
  constructor-registered `effect()` watching `AuthService.currentUser()` (itself a computed signal already
  re-derived from the one `tokens` signal `AuthService` owns) - no explicit logout hook needed. This also
  closes the panel, so a freshly-logged-in different user never sees the previous session's conversation
  flash by before it clears.
- **No persistence**: nothing here is ever written to `localStorage`/`sessionStorage` - closing the browser
  tab loses the conversation, by design (see "Clarification and conversation state" below, which already
  documented this for the backend side; the frontend now makes the identical choice for its own richer
  chat state).

**Conversation is a flat, chronological message list** (`ChatMessage[]`), not a single "current state" the
whole screen swaps per response the way the original one-shot form worked: a user bubble, an assistant
bubble (a clarification question, a `NoSuitableResource`/`Disabled`/`ProviderUnavailable` notice, or a
retryable transport-failure notice), or a proposal card, appended in order and never removed. A fresh
`propose()` call is built from this same history reduced back down to the bounded, wire-level
`ConversationTurn[]` contract (see "Clarification and conversation state" below for exactly how) - the UI
being richer than the wire contract does not change what the backend ever sees or how it's bounded.

**Only one proposal is ever actionable at a time** (`AiAssistantService.activeProposalId`): asking a
follow-up question that produces a NEW proposal marks the previous one `superseded` - its card stays
visible in the transcript (so the conversation still reads coherently), but its Confirm/Discard actions
are gone. A proposal's own lifecycle is independent of the conversation around it:
`pending -> confirming -> confirmed | pendingApproval` (success, terminal, a "View in My Bookings" link
appears and Confirm is permanently disabled) or `pending -> confirming -> failed` (the slot was taken
etc. - terminal for that exact candidate, shown inline on the card; the user asks again for a fresh one,
rather than retrying a candidate now known to be stale) or `pending -> discarded` (the user dismissed it
explicitly, conversation continues). None of this - including `confirming`'s own progress state - is
ever reachable except through the one real `POST /api/bookings` call; no status is ever synthesized
client-side.

**Accessibility**: the panel is `role="dialog"` with an accessible name, but deliberately **not**
`aria-modal` and has no focus trap - it does not block interaction with the rest of the page the way a true
modal would (no backdrop), so full modal semantics would misrepresent it. The launcher exposes
`aria-expanded`/`aria-controls`/`aria-haspopup="dialog"`; focus moves into the composer on open and back to
the launcher on close; `Escape` closes the panel. A single `aria-live="polite"` region (visually hidden)
announces only the newest assistant message or "Assistant is typing…" - never the whole transcript, which
the ordinary (non-live) message list renders without re-announcing on every change. The message area
auto-scrolls to new content only when the user was already scrolled near the bottom; otherwise a small
"New messages" affordance appears instead of yanking a reading user back down.

**The `/bookings/ask-ai` route still exists**, but only as a thin redirect (`AskAiComponent`): visiting it
opens the shared widget and navigates to My Bookings, rather than rendering a second, separate chat
implementation on a page of its own - existing links/bookmarks (including My Bookings' own "Ask AI to
book" link) keep working unchanged.

Every message's text is rendered through Angular's default interpolation, never `[innerHTML]` - model and
user text is always plain text, never parsed as markup, by construction rather than by a sanitization step
that could be gotten wrong.

## Architecture

```
AiAssistantWidgetComponent / AiAssistantService (frontend - floating assistant, see below)
        │ POST /api/ai-bookings/propose
        ▼
AiBookingsController → ProposeBookingCommandRequest/Handler (BookSpace.Application/AiBooking/)
        │                       │
        │ IBookingIntentInterpreter (abstraction)
        │                       │
        │               GeminiBookingIntentInterpreter (BookSpace.Infrastructure/AiBooking/)
        │                       │ HTTPS, structured JSON output
        │                       ▼
        │              Google Gemini API (generateContent)
        │
        │ reuses, read-only: IResourceTypeRepository, IResourceRepository,
        │   IAvailabilityRuleRepository, IBlackoutPeriodRepository,
        │   IBookingAvailabilityRepository, BookingEligibilityChecker (internal,
        │   the SAME function CreateBookingCommandHandler itself calls)
        ▼
ProposeBookingResponse (Proposed | NeedsClarification | NoSuitableResource | Disabled | ProviderUnavailable)
        │
        │ user reviews, clicks Confirm
        ▼
POST /api/bookings → CreateBookingCommandRequest/CreateBookingCommandHandler (UNCHANGED, pre-existing)
        │  (IResourceBookingLock, BookingEligibilityChecker, approval branch, concurrency - all real)
        ▼
Booking row (Confirmed or Pending)
```

### Why a proposal cannot create anything (the structural guarantee)

`ProposeBookingCommandHandler`'s constructor takes `IBookingIntentInterpreter`, `IOptions<AiBookingOptions>`,
`ITenantRepository` (read), `IResourceTypeRepository` (read), `IResourceRepository` (read),
`IAvailabilityRuleRepository` (read), `IBlackoutPeriodRepository` (read), `IBookingAvailabilityRepository`
(read), `ICurrentUserContext`, `TimeProvider`, and a logger - **no** `IBookingRepository`, no
`INotificationOutboxWriter`, no `IApprovalRequestRepository`, no `IResourceBookingLock`. There is no
method call through which `Handle()` could possibly persist a `Booking`/`ApprovalRequest`/
`NotificationOutboxItem` even if every validation step were skipped - this is enforced by what the class
is even capable of calling, not merely by what it currently chooses to call. `BookingEligibilityChecker`
(`BookSpace.Application.Bookings`, `internal`) is called directly, read-only, exactly as
`CreateBookingCommandHandler` itself calls it - the two can never silently disagree about what counts as
available, because they are the literal same function.

There is deliberately no "/confirm" endpoint, no proposal id, no "already validated" flag sent back to
the frontend and trusted on the way in. Confirmation is simply the frontend calling the pre-existing
`POST /api/bookings` with the candidate's own fields - from the backend's point of view, it is
indistinguishable from a human having typed those exact values into the manual booking form.

## Resolution pipeline (what the backend authoritatively re-checks)

The model's structured output (`LlmBookingIntent`) is treated as **untrusted input** throughout - every
field is independently validated or re-derived, never taken at face value:

1. **Tenant membership**: `TenantMembershipGuard.EnsureTenantMember` - the global `SysAdmin` is rejected
   (`409 Common.TenantMembershipRequired`), the same guard every other tenant-member-only action in this
   codebase uses. `TenantId`/`UserId` are read exclusively from `ICurrentUserContext` (the validated JWT),
   never from the prompt or the model's output - there is no `TenantId`/`UserId` field in `LlmBookingIntent`
   at all.
2. **Feature flag**: if `AiBooking:Enabled` is `false`, the interpreter is never called at all - returns
   `Disabled` immediately.
3. **Interpretation**: the interpreter is called with a bounded timeout
   (`AiBookingOptions.RequestTimeoutSeconds`, default 15s) and the caller's own `CancellationToken`, linked
   together - a timeout or failure (network, non-2xx, malformed/unparseable response) becomes
   `ProviderUnavailable`, logged as a technical outcome only (exception type, status code - never the
   prompt or the response body).
4. **Date**: `date` (`yyyy-MM-dd`) is strictly parsed first, on its own - a missing/malformed date, or one
   earlier than "now" (the backend's own `TimeProvider`, re-derived in the resolved resource's own time
   zone once known - never the model's own notion of "today"), produces `NeedsClarification` immediately,
   before anything else below even runs.
5. **Quantity vs. party size** (see its own section below): resolved by the backend, deterministically -
   never assumed from the model's own arithmetic.
6. **Resource resolution**: `resourceTypeHint`/`resourceNameHint` are fuzzy-matched (case- and
   diacritic-insensitive - see "Bosnian without diacritics" below) against this tenant's **own**,
   tenant-filtered resource types/resources - never trusted as an already-resolved id. A hint that matches
   no real type/resource resolves to `NoSuitableResource` directly (never silently "widened" to search
   everything, which could let a nonexistent name accidentally resolve to an unrelated real one). Zero
   matches -> `NoSuitableResource`; exactly one -> proceed; more than one -> `NeedsClarification`, naming
   every candidate so the user can pick (or re-ask with a distinguishing name). **Resolved before start/end
   time** (a deliberate ordering, not top-to-bottom field validation) - see step 8 below for why.
7. **Cross-zone ambiguity**: if the resolved resource's own time zone differs from the tenant's
   `DefaultTimeZoneId` (the zone the date/time phrase was interpreted against) AND the date was a
   *relative* expression (`dateIsRelativeExpression`, from the model) rather than an explicit date,
   `NeedsClarification` is returned instead of guessing which zone "today"/"next Tuesday" was meant in.
8. **Day-wide availability, THEN start/end time**: once the date and the resource are both known, the
   backend checks whether the resource has **any** `AvailabilityRule` covering that calendar date's day of
   week at all (`AvailabilityCalculator.ComputeOpenPeriodsUtc` - the exact same function the final
   eligibility check below also uses, so this can never disagree with it). **This check runs
   unconditionally at that point - not only when a start time is still missing.** A multi-round
   clarification conversation can easily end up with a start time already answered in an EARLIER round,
   before the resource itself finally resolves to exactly one match in a LATER one (e.g. the model asks
   "which room?" last); gating this check on "no start time yet" would then never fire for that ordering,
   and the request would sail through every time-related check only to discover the closed day at the very
   end of the full eligibility check - after already asking for, and receiving, a time for nothing (a real
   bug found and fixed during manual testing). Checking immediately once date+resource are known, regardless
   of what else is already filled in, is what reliably skips the doomed clarification question. If the
   resource is closed the entire day, the result is `NoSuitableResource` ("this resource isn't open that
   day") **without the end-time/duration/eligibility checks ever running**. Otherwise (the resource is open
   at some point that day), `startTime`/`endTime` (`HH:mm`)/`durationMinutes` are parsed as before: a
   missing start time, or a missing end time AND duration together, produce a specific `NeedsClarification`
   question - never a guessed default. **The actual local-to-UTC conversion is always
   `AvailabilityCalculator.ConvertLocalToUtc`** - the exact function
   `GetResourceAvailabilityQueryHandler`/`CreateBookingCommandHandler` already use, inheriting its existing,
   already-proven DST policy (spring-forward normalizes forward; fall-back resolves to the standard,
   post-transition offset) - see `docs/availability-and-timezones.md`. The model never performs timezone
   conversion itself; it only ever emits a local calendar date and a local time-of-day, in the one reference
   zone it was told to use (see "Reference time zone" below). This day-wide check deliberately does **not**
   also look at `BlackoutPeriod`s - a full-day blackout is a different concept ("unavailable", not "outside
   working hours") and, unlike a missing `AvailabilityRule`, a specific time might still reasonably be asked
   about (e.g. a different, non-blacked-out part of the day) - a blackout alone still falls through to
   asking for a time, exactly as before this change.
9. **Eligibility**: `BookingEligibilityChecker.CheckAsync` (status -> availability -> blackout -> capacity,
   identical order to `CreateBookingCommandHandler`) against the resource's real `AvailabilityRule`s,
   `BlackoutPeriod`s, and currently-active bookings - the `AvailabilityRule`s/`BlackoutPeriod`s were already
   fetched once in step 8 above and are reused here, not fetched a second time. Not eligible ->
   `NoSuitableResource`, with the same rejection-reason wording the manual flow's own conflicts use.
10. **Proposed**: only if every step above passed. The candidate carries `RequiresApproval` as-is from the
    resource - the AI never decides or overrides this; confirming still goes through the exact same
    Pending/approval branch `CreateBookingCommandHandler` already has.

## Reference time zone for relative expressions

"Tomorrow"/"next Tuesday"/"sutra"/"sljedeći utorak" and similar relative expressions are interpreted
against the **tenant's `DefaultTimeZoneId`** (the same field already used for the tenant's platform-level
defaults) and the backend's own current instant (`TimeProvider`, not `DateTime.Now`, so this is
deterministically testable) - this is computed and handed to the model as an explicit reference point
("today is <date>, <day-of-week>, <time>, zone <id>") before any interpretation happens; the model is
instructed to resolve relative expressions against exactly that reference and to emit its answer as a
plain local date/time in that same zone, never to attempt a timezone conversion itself. Once a specific
resource is resolved, the **resource's own `TimeZoneId`** becomes authoritative for the final UTC
conversion (matching every other resource-local wall-clock concept in this codebase - see
`docs/availability-and-timezones.md`) - see "Cross-zone ambiguity" above for the one case this can make
genuinely ambiguous, and how it's handled (ask, never guess).

## Quantity vs. party size

`Resource.Capacity`/`Booking.Quantity` have no built-in "this is an occupancy headcount" vs. "this is a
pool of interchangeable units" distinction anywhere else in this codebase - inspecting the actual model
(`docs/resource-lifecycle-and-capacity.md`) shows `Quantity` is simply however much of `Capacity` one
booking consumes. This task's own two worked examples ("a lab **for 3 people**") read naturally as
*booking the one room*, which must be able to **seat** 3 people - not three separate bookings of it. The
policy this implementation encodes (`ProposeBookingCommandHandler.ResolveQuantity`/
`ResolveResourceCandidates`):

- An **explicit** `quantity` in the model's output (the user asked for a specific number of *units* of the
  resource itself, e.g. "book 2 desks") is used exactly as given, and is never influenced by `partySize`.
- A **`partySize`** alone ("for 3 people") never becomes `Quantity = 3`. It defaults `Quantity` to `1`
  (book the one resource) and instead **narrows which resource qualifies** by requiring
  `Resource.Capacity >= partySize` - a lab that can only seat 2 is not offered for a 3-person request; if
  no active resource of the matched type has enough capacity, the result is `NoSuitableResource`, not a
  booking that silently under-serves the stated headcount.
- Neither given: `Quantity` defaults to `1`, matching every other booking-creation default in this
  codebase (`CreateBookingCommandRequest(..., int Quantity = 1)`).

This is a judgment call this codebase's existing schema does not settle definitively on its own -
documented here explicitly, the same way `docs/bookings-and-concurrency.md` §4 documents its own 409-vs-
other-status judgment call, rather than left implicit.

## Language

Two detection paths:

- **Normal path**: the model itself reports `language` (`"bs"` or `"en"`) as part of its structured
  output, since it has read the whole request and is far better positioned to judge than any local
  heuristic. Validated against the fixed two-value enum before use - an unrecognized value falls back to
  the heuristic below.
- **Fallback path** (`BookingLanguageHeuristic`, `BookSpace.Application/AiBooking/`): used only for the two
  outcomes where no model response was ever obtained at all (`Disabled`, `ProviderUnavailable`) - a small,
  conservative check for Bosnian diacritic letters (č/ć/š/đ/ž) and a short list of common Bosnian words,
  checked both with and without diacritics (see "Bosnian without diacritics" below). Defaults to English
  when neither signal is present.

Every clarification/error message the backend itself generates (missing date, malformed input, no
suitable resource, disabled, provider unavailable, multiple candidates, cross-zone ambiguity) is
templated in both languages (`ProposeBookingCommandHandler`'s private `*Message` methods) - deterministic,
not a second LLM call, so these messages can never themselves be mis-phrased by a model error. UI chrome
(buttons, labels, page titles) stays in English, matching this application's existing, English-only
approach everywhere else (no i18n framework is used anywhere in this codebase) - only the assistant's own
generated text (clarification questions, the proposal summary, these templated messages) mirrors the
user's request language, per the task's own requirement.

### Bosnian without diacritics

Resource/resource-type name matching (`TextNormalization.FoldToPlainLatinLowercase`) and the language
heuristic both fold text through Unicode NFD decomposition (stripping general Latin diacritical marks)
plus an explicit `đ/Đ -> d/D` map (the one Bosnian letter NFD does not decompose, since it has no accent-
letter composition - it is its own code point) - so "učionica"/"Ucionica"/"UCIONICA" and "đačka
soba"/"dacka soba" are all treated identically. The model is also explicitly told it may receive Bosnian
without diacritics and must still interpret it correctly - this is primarily an interpretation task the
model itself handles (it is good at recognizing "sljedeci utorak" as "sljedeći utorak" and
"ucionica"/"laboratorija" as what they mean), with `TextNormalization`'s own fold existing specifically so
the backend's OWN subsequent resource-name matching doesn't reintroduce a diacritic-sensitivity bug after
the model has already correctly understood the request.

## Clarification and conversation state

No AI conversation is persisted anywhere (no new table, no migration) - there was no demonstrated need for
one, per the task's own "avoid persistence... unless there is a demonstrated need." The entire conversation
state is the small, bounded transcript the FRONTEND itself holds (`AiAssistantService.messages`, the richer
chat transcript - see "Floating assistant" above) and reduces back down to
(`ProposeBookingCommandRequest.PriorTurns`, capped at `MaxPriorTurns` = 6 turns of up to
`MaxTextLength` = 500 characters each - `ProposeBookingCommandRequestValidator`) on every round-trip. This
is what lets a clarification reply ("next Tuesday") be understood in the context of the original request
("I need a room") without the user retyping it, while keeping the backend itself fully stateless between
calls and bounding how much untrusted text one conversation can ever accumulate.

**Found against the real Gemini API, not caught by the fake-provider tests**: `MaxPriorTurns` = 6 only
allows 3 clarification round-trips. The system prompt now explicitly tells the model to combine every
missing piece of information into ONE question rather than asking about them one field at a time (see
`GeminiBookingIntentInterpreter.BuildSystemPrompt`) - a real model is otherwise free to ask "what date?",
then (once answered) "what time?", then "which room?" as three separate rounds, burning through the turn
budget on a single moderately-vague request. On top of that prompt fix, `AiAssistantService.buildContextTurns()`
now reduces the conversation to a **sliding window** of the most recent `MAX_CONTEXT_TURNS` (= 6, mirroring
the backend exactly) before every request, rather than the original one-shot form's hard stop (refusing to
send once the limit was reached, with a "please start over" message) - a real chat should keep flowing, and
trimming the oldest turns structurally makes the backend's 400 impossible to trigger from the frontend at
all, rather than merely front-running it with a client-side check.

## LLM integration

**Provider**: Google's Gemini API, called directly over its native REST contract -
`POST {BaseUrl}models/{Model}:generateContent` (`BaseUrl` defaults to
`https://generativelanguage.googleapis.com/v1beta/`) - with structured JSON output
(`generationConfig: {responseMimeType: "application/json", responseSchema: {...}}`). Authentication is the
`x-goog-api-key` HTTP header, **never** a query-string API key (which would land in access/proxy logs
wherever this request is observed). No Google SDK/client library is used - this is a plain `HttpClient`
call through the existing `IBookingIntentInterpreter` abstraction, the same shape every other outbound
integration in this codebase already uses, confirmed against Google's own current API documentation before
implementing (`ai.google.dev/api/generate-content`, `ai.google.dev/gemini-api/docs/structured-output`,
`ai.google.dev/gemini-api/docs/models`, `ai.google.dev/gemini-api/docs/rate-limits`), per the task's own
instruction to check it.

**Originally built against OpenAI** (Chat Completions API with Structured Outputs) and migrated to Gemini
because the OpenAI key available for this project could not be billed - see git history for the prior
implementation. The provider-agnostic abstraction (`IBookingIntentInterpreter`) and everything above it
(`ProposeBookingCommandHandler`, the structured `LlmBookingIntent` contract, the entire resolution
pipeline) did not need to change; only `GeminiBookingIntentInterpreter` itself is provider-specific.

**Model choice**: `gemini-3.5-flash-lite` (the `AiBooking:Model` default) - a small, fast, Free-Tier-
eligible text model, chosen as the closest Gemini equivalent to the "small, cost-effective" role
`gpt-4o-mini` played before. **This was not guessed**: as of 2026-10-08, Google's own model/pricing
documentation lists `gemini-3.5-flash-lite` (alongside `gemini-3.8-flash`) as the models explicitly
recommended for *new* projects, with the prior `gemini-2.5-*` generation now restricted to projects that
already used it. **Model lineups and Free Tier eligibility change over time and are not something this
implementation can guarantee going forward** - before relying on this long-term, re-check
<https://ai.google.dev/gemini-api/docs/models> and <https://ai.google.dev/gemini-api/docs/pricing> for the
current recommended model and its Free Tier status, and update `AiBooking:Model` (one config value, no code
change) if it has moved on.

**Important, honestly reported**: this environment had no real Gemini API key available, so
`GeminiBookingIntentInterpreter` was implemented against Gemini's published documentation but was **never
exercised against the real API** - see "What was NOT verified" below for exactly what a real key still
needs to confirm.

- **Structured output schema**: Gemini's `responseSchema` is its own restricted `Schema` object, **not**
  plain JSON Schema and **not** the same dialect OpenAI's strict `json_schema` mode used - two differences
  that matter enough to call out explicitly, the second one confirmed the hard way against the real API:
  - `type` values are Gemini's own `Type` enum JSON names, **uppercase** (`OBJECT`, `STRING`, `INTEGER`,
    `BOOLEAN`, `ARRAY`, `NUMBER`) - not lowercase JSON-Schema-style strings. An earlier, lowercase version
    of this schema was rejected outright by the real Gemini API with `HTTP 400 INVALID_ARGUMENT` before
    this was corrected - documentation fetches on this exact point were inconsistent/unreliable (see
    "LLM integration" above and the git history of this file), so this was only actually confirmed once
    tried for real.
  - There is **no** `additionalProperties` keyword - unlike OpenAI's strict mode, Gemini's `Schema` object
    does not document it at all. Optionality is instead expressed purely via the boolean `nullable`
    keyword next to a single `type`, never via a type array like `["string", "null"]`; and only the fields
    the model always fills in (`language`, `status`) are listed in `"required"` - every other field is
    simply omitted from `"required"` when the model has nothing to put there, with no "null-as-a-value"
    workaround needed, and nothing downstream relies on `additionalProperties` for safety regardless (every
    field is independently re-validated server-side - see "Resolution pipeline" above).
  - `propertyOrdering` (Gemini-specific) asks the model to fill fields in a fixed order (the same order the
    fields are documented in throughout this file), which in practice makes later fields (e.g.
    `startTime`) more reliable once earlier ones (e.g. `date`) have already been committed to output.
- `temperature: 0` (deterministic-as-possible interpretation, not creative text generation).
- The deserialized result is independently validated field-by-field regardless of the schema having
  "already" constrained it (see "Resolution pipeline" above) - structured output narrows what shape comes
  back, it does not make the content trustworthy.
- **Safety and truncation handling**: a blocked *prompt* (`promptFeedback.blockReason` - e.g. `SAFETY`,
  `PROHIBITED_CONTENT`), no candidates at all, a candidate whose `finishReason` is anything other than
  `STOP` (`MAX_TOKENS` - truncated before valid JSON could complete; `SAFETY`/`RECITATION` - the candidate
  itself was blocked after generation; `OTHER`), missing/empty response text, or malformed JSON in the
  response text all become `BookingIntentInterpretationException` - **never** a fabricated successful
  proposal built from a partial/unsafe response.
- A malformed/unparseable outer response, any non-2xx HTTP status, or a network failure also become
  `BookingIntentInterpretationException`, mapped to the `ProviderUnavailable` outcome - never a raw 500. A
  non-2xx status is logged with only the HTTP status code and Gemini's own short `status` classifier (the
  standard `google.rpc.Status` enum every Google API error uses - e.g. `RESOURCE_EXHAUSTED`,
  `UNAUTHENTICATED`, `INVALID_ARGUMENT`, `PERMISSION_DENIED`, `UNAVAILABLE`) - never the error `message`
  text, which could in principle echo back request content.
- **No retries inside the interpreter.** `ProposeBookingCommandHandler` calls it exactly once, bounded by
  `AiBookingOptions.RequestTimeoutSeconds`; a failure is reported once, never retried automatically - see
  "Limits and rate limiting" below.
- **Prompt-injection posture**: the system instruction explicitly tells the model to treat the user's own
  text as content to interpret, never as instructions to it, and to never invent a resource type outside
  the tenant's real, provided list. This is advisory, not the actual security boundary - the REAL boundary
  is structural: every field of the model's output is re-validated/re-resolved against real tenant data
  server-side regardless of what the prompt asked for or what the user's text tried to claim (see
  "Resolution pipeline"). A user's own free text, or a resource's name/description, can therefore never
  override authorization or system instructions, because nothing downstream ever trusts the model's output
  without independently re-deriving it from real data.
- **Minimum necessary data sent**: the user's own request text (and their own prior-turn replies, mapped to
  Gemini's `user`/`model` roles - Gemini has no `assistant` role), the tenant's resource TYPE NAMES (not
  individual resource names/descriptions/capacities/approval config, not other users' bookings, never an
  auth token, never an email address, never any other user's data). Per Google's current Gemini API Free
  Tier terms, content sent to the Free Tier may be used by Google to improve its products - this data
  minimization is what keeps that exposure to resource type names and the user's own free text, never
  another user's data; **prefer synthetic/test data (not real customers' resource names or request text)
  when manually testing this feature locally against the real Free Tier API**, and re-confirm the current
  terms at <https://ai.google.dev/gemini-api/terms> before sending anything sensitive.

## Configuration

Bound from the `AiBooking` configuration section (`AiBookingOptions`/`AiBookingOptionsValidator`,
`BookSpace.Application/AiBooking/`), registered with the same `AddOptions<T>().Bind(...).ValidateOnStart()`
convention as every other options class in this codebase (`EmailOptions`, `BackgroundJobsOptions`,
`CalendarFeedOptions`, `AppOptions`):

| Key | Default | Required when |
|---|---|---|
| `AiBooking:Enabled` | `false` | always has a default - the feature is off until explicitly turned on |
| `AiBooking:ApiKey` | *(empty)* | `Enabled=true` - a **Gemini** API key (get one free at <https://aistudio.google.com/apikey>), never an OpenAI key |
| `AiBooking:Model` | `gemini-3.5-flash-lite` | `Enabled=true` - a Gemini model name, see "Model choice" above |
| `AiBooking:BaseUrl` | `https://generativelanguage.googleapis.com/v1beta/` | `Enabled=true` (must be an absolute http(s) URL) |
| `AiBooking:RequestTimeoutSeconds` | `15` | `Enabled=true` (1-60) |
| `AiBooking:MaxOutputTokens` | `400` | `Enabled=true` (1-2000) |

The key/value names are kept provider-agnostic (`ApiKey`/`Model`, not `GeminiApiKey`/`GeminiModel`) because
this codebase only ever configures one LLM provider at a time, the same way `Email:Smtp:Password` doesn't
encode "Gmail" in its own name - see `AiBookingOptions`'s own doc comment.

**Disabled by default, everywhere** - exactly like `Email:Enabled`. With `AiBooking:Enabled=false` (the
default in `appsettings.json`, and in every environment this codebase already runs in - Development,
Testing, and any existing container/Azure/AWS deployment that predates this feature), the app starts and
operates completely normally with zero AiBooking configuration: `AiBookingOptionsValidator` only requires
`ApiKey`/`Model`/etc. when `Enabled` is `true` (same gating pattern as `EmailOptionsValidator`), and
`ProposeBookingCommandHandler` returns `Disabled` immediately, before ever touching
`IBookingIntentInterpreter` - `AiBookingsController`'s one action is reachable and answers normally either
way. `GeminiBookingIntentInterpreter`/its `HttpClient` are still registered in DI unconditionally (the same
pattern `INotificationSender`/`SmtpNotificationSender` already use for `Email:Enabled=false`) - this costs
nothing at startup and makes no network call until actually invoked.

**Where the API key is stored and how it reaches the backend**:

- **Local `dotnet run`**: `dotnet user-secrets set "AiBooking:ApiKey" "AIza..."` from `backend/BookSpace.Api`
  - never committed, matching how `Auth:SigningKey`/`Email:Smtp:Password` already work locally.
- **Local Podman/Docker Compose** (`compose.local.yaml`, `compose.production-local.yaml`): an
  `AiBooking__ApiKey` value in your own untracked `.env`/`.env.production-local` file (see
  `.env.example`/`.env.production-local.example` for the documented placeholder) - passed through to the
  container exactly like `Email__Smtp__Password` already is, defaulting to empty/disabled when unset.
- **The real AWS deployment** (`.github/workflows/docker-image.yml`'s `deploy` job, an EC2 instance behind
  SSM): this repository does **not** contain the actual production `compose.yaml` the EC2 host runs - it
  lives only on that host (`/home/ec2-user/bookspace/compose.yaml`), outside version control, and the
  deploy workflow only ever exports `BOOKSPACE_IMAGE`/`DB_CONNECTION_STRING` (the latter fetched from AWS
  Secrets Manager) before running `docker compose up` there - every OTHER environment variable the running
  container needs (`Auth__SigningKey`, `Bootstrap__*`, and a future `AiBooking__ApiKey`) is therefore
  already expected to live in that host's own, already-existing configuration, which this workflow does not
  manage or recreate. **Enabling real AI-assisted booking on that deployment requires a deliberate,
  separate follow-up** - outside this task's scope, and NOT performed here (`Do not modify AWS resources or
  perform deployment` was an explicit constraint): add `AiBooking__ApiKey` as a new AWS Secrets Manager
  secret and fetch/export it in the `deploy` job exactly the way `DB_CONNECTION_STRING` already is, then
  add it to that host's own `compose.yaml` `environment:` block (or add it there directly as a plain
  value if the team's own secret-handling policy for that host allows it - this document does not decide
  that policy). No AWS resource, GitHub secret, or the workflow file itself was modified by this task.

## Limits and rate limiting

- **Input length**: `Text` and every `PriorTurns[].Text` are capped at 500 characters
  (`ProposeBookingCommandRequestValidator.MaxTextLength`); at most 6 prior turns
  (`MaxPriorTurns`) - a 400 `ValidationProblemDetails` beyond either, the same existing FluentValidation
  pipeline every other request in this codebase already uses.
- **Request duration**: bounded by `AiBooking:RequestTimeoutSeconds` (default 15s, max 60s) via a
  `CancellationTokenSource` linked with the caller's own token - a timeout reports `ProviderUnavailable`,
  never hangs the HTTP request indefinitely.
- **Output size**: bounded by `AiBooking:MaxOutputTokens` (Gemini's own `generationConfig.maxOutputTokens`).
- **Request frequency**: a new named rate-limiter policy, `"ai-booking"` (`Program.cs`, the same
  `AddRateLimiter`/`FixedWindowRateLimiter` mechanism `AuthController.Login`'s existing `"login"` policy
  already uses) - 6 requests/minute, partitioned by the caller's own JWT `sub` claim (not by IP, unlike
  login, since this endpoint is already authenticated) with `QueueLimit = 0` (an immediate 429, never a
  queued wait). Raised to effectively unlimited in the `Testing` environment, exactly like the login policy
  already is, for the identical reason (`WebApplicationFactory`'s `TestServer` would otherwise collapse an
  entire integration suite's traffic into one partition).
- **No unlimited retries anywhere**: `OpenAiBookingIntentInterpreter` never retries internally;
  `ProposeBookingCommandHandler` calls it exactly once per `propose()` HTTP request. A user-initiated retry
  (the frontend's own "Try again" button) is the only way a second attempt ever happens.

## Untrusted output and tenant isolation - summary of the guarantees

- `TenantId`/`UserId` come exclusively from `ICurrentUserContext` (the validated JWT) - never from the
  request body, the prompt, or the model's output; `LlmBookingIntent` has no such fields to even tempt
  this.
- Resource resolution only ever matches against `IResourceTypeRepository`/`IResourceRepository`, which rely
  on `BookSpaceDbContext`'s existing global, fail-closed tenant query filter (`ITenantOwned`) - the exact
  same mechanism every other feature in this codebase already relies on, not a new one. A hint that happens
  to name another tenant's resource can never resolve to it; see
  `BookSpace.Api.Tests/AiBooking/AiBookingsEndpointsTests.Propose_WhenTheHintOnlyMatchesAnotherTenantsResource_ReturnsNoSuitableResource`
  for the real, two-seeded-tenant proof.
- Malformed JSON, an invented resource, an out-of-range quantity, a nonexistent/past date, and an unclear
  request are all handled as ordinary, expected outcomes (`NeedsClarification`/`NoSuitableResource`/
  `ProviderUnavailable`) - never an unhandled exception, never a 500, never something silently guessed.
- The model is never given write access to anything - no tool/function-calling capability that could
  invoke a booking action is wired up; it can only ever return the fixed JSON schema described above.
- No model-generated SQL or code is ever executed - `LlmBookingIntent`'s fields are plain strings/
  numbers/booleans, parsed with strict format checks (`DateOnly.TryParseExact`/`TimeOnly.TryParseExact`),
  never interpolated into a query or evaluated as code.

## Known limitations

- Resource matching is a simple case-/diacritic-insensitive substring match against resource type/name -
  not a semantic/fuzzy search. A sufficiently creative misspelling may fail to match a real resource and
  report `NoSuitableResource` rather than finding it.
- The cross-zone ambiguity check (see "Reference time zone") only fires for a *relative* date expression
  against a resource whose zone differs from the tenant default - an absolute/explicit date is assumed
  unambiguous regardless of zone, and a single combined tenant-wide `DefaultTimeZoneId` is the only
  reference point used before a resource is chosen (no attempt to disambiguate "today" across multiple
  differently-zoned candidate resources before one is picked).
- The proposal step never acquires `IResourceBookingLock` (by design - see "Why a proposal cannot create
  anything") - between a `Proposed` result and the user's confirm click, the slot can be taken by someone
  else; this is reported as an ordinary `409` conflict at confirmation time (proven in
  `AiBookingsEndpointsTests.Propose_ThenTheSlotFillsUpBeforeConfirmation_ConfirmingIsRejectedByTheRealCapacityCheck`),
  never silently retried or hidden.
- Only one booking per request (recurring bookings are explicitly out of scope for this task).
- No AI conversation is ever persisted - closing/reloading the tab loses the in-progress clarification
  transcript (by design; see "Clarification and conversation state").
- A conversation long enough to exceed `MAX_CONTEXT_TURNS` (6 - see "Clarification and conversation state")
  drops its OLDEST turns from what's sent to the model, including, in an extreme case, the original request
  itself - the system prompt's "combine every missing piece into one question" instruction keeps real
  conversations well under this in practice, but it is a sliding window, not an unbounded memory.
- The floating assistant's desktop/mobile layout, focus management, and scroll behavior were verified via
  the Angular test suite (jsdom) only - see "What was verified vs. what was NOT" below for what a real
  browser pass still needs to confirm.

## What was verified vs. what was NOT (real provider)

**Verified in this environment** (deterministic, fake-provider-backed, or - for
`GeminiBookingIntentInterpreter`'s own request/response handling - a fake `HttpMessageHandler`): the full
request -> clarification/proposal -> explicit-confirmation flow; tenant isolation; reuse of the real
booking engine and its concurrency/approval behavior; date/time/DST handling via the existing,
already-proven `AvailabilityCalculator.ConvertLocalToUtc`; the `Disabled`/timeout/failure paths; the
frontend's explicit-confirmation and stale-response-ignoring behavior; `GeminiBookingIntentInterpreter`'s
own request shape (auth header, structured-output config, role mapping), response mapping, and handling of
a blocked prompt, a non-`STOP` finish reason, an empty/malformed response, and HTTP 400/401/403/404/429/5xx
errors - all against a fake transport. See "Tests" below for the full inventory. **None of this required,
or made, a real call to Gemini (or, before the migration, to OpenAI)** - every automated test runs against
either `FakeBookingIntentInterpreter` or a fake `HttpMessageHandler`, matching the task's own "CI must not
call a real paid LLM API" requirement.

**NOT verified - requires a real API key, manually, before relying on this in production**:

- That `GeminiBookingIntentInterpreter`'s exact request shape (`generationConfig.responseSchema` and its
  Gemini-specific JSON Schema subset, the `x-goog-api-key` header, the `models/{model}:generateContent`
  path) is accepted as-is by the real Gemini API without a schema/transport error - this was built from
  Gemini's published documentation, not exercised against the live endpoint.
- That `gemini-3.5-flash-lite` is still the current, Free-Tier-eligible, recommended model by the time this
  is tried for real - re-check <https://ai.google.dev/gemini-api/docs/models> first (see "Model choice"
  above); a model name that has since been retired would surface as an HTTP 404 from
  `GeminiBookingIntentInterpreter`, handled safely (`ProviderUnavailable`), but obviously not usefully.
- The real language quality: whether `gemini-3.5-flash-lite` actually interprets Bosnian (with and without
  diacritics) as reliably as English for this task's two worked examples and realistic variations -
  **mocked-provider tests prove the BACKEND's handling of a given structured response; they prove nothing
  about whether a real model would actually produce a good one for a given sentence.** This distinction
  must not be blurred when reporting this feature as "done."
- Real latency/timeout behavior against the live API, and real behavior under Gemini's own Free Tier rate
  limiting - Google's own current documentation does **not** publish fixed free-tier RPM/TPM/RPD numbers;
  they are account/tier-dependent and visible live only in Google AI Studio
  (<https://aistudio.google.com/rate-limit>) - so no specific quota is promised here, only that the
  rate-limiter/timeout/no-retry behavior documented above holds regardless of what that quota turns out to
  be.
- To verify manually: set `AiBooking__Enabled=true` and a real `AiBooking__ApiKey` (a Gemini key - see
  "Configuration" above for exactly where, per environment), then try both worked examples from the top of
  this document, plus at least one deliberately vague request (to see a real clarification question) and
  one request naming a nonexistent resource. Use synthetic resource/request data, not real customer data -
  see "Minimum necessary data sent" above.
- **The floating assistant's actual browser behavior** - no browser automation tool (Playwright, Chrome
  DevTools, etc.) was available in the environment this was built in, so desktop/mobile layout, the virtual
  keyboard on a real mobile device, actual focus-ring visibility, and real screen-reader announcement were
  **not** verified visually or interactively. What WAS verified (jsdom, via `ai-assistant-widget.spec.ts`
  and friends): the DOM structure and classes a real layout would render from, `aria-expanded`/
  `aria-controls`/`role`/`aria-label` attribute values, focus actually moving to specific elements on
  open/close, and keyboard event handling (Enter/Shift+Enter/Escape/IME composition) - jsdom does not lay
  out CSS or render pixels, so none of this proves the panel actually LOOKS right at any given viewport
  width, that `env(safe-area-inset-bottom)` behaves on a real notched device, or that a real screen reader
  announces the live region the way `aria-live="polite"` is intended to. **Before relying on this,
  manually check it in a real desktop browser and on a real or emulated mobile viewport.**

## Tests

- `BookSpace.Application.Tests/AiBooking/`: `ProposeBookingCommandHandlerTests` (28 tests - valid request,
  disabled, provider failure/timeout, SysAdmin rejection, explicit model clarification, missing/malformed/
  past date, missing start time (both when the resource is open that day - still asks for a time - and
  when it's closed the entire day, by day-of-week or by having no rule at all - immediately
  `NoSuitableResource`, never asking for a time), missing end+duration, duration-instead-of-end-time,
  end-before-start, invented resource type, name hint matching nothing in this tenant's own catalog, party
  size exceeding every candidate's capacity, multiple matching resources, explicit quantity overriding
  party size, ineligible candidate via the real `BookingEligibilityChecker`, Bosnian/English/unsupported-
  language handling, cross-zone clarification both ways, a deterministic DST spring-forward case using the
  same dynamically-computed `Europe/Sarajevo` transition-date convention `availability-and-timezones.md`
  documents), `BookingLanguageHeuristicTests`, `TextNormalizationTests`, `AiBookingOptionsValidatorTests`,
  `ProposeBookingCommandRequestValidatorTests` - 60 tests total, all against `FakeBookingIntentInterpreter`.
- `BookSpace.Api.Tests/AiBooking/AiBookingsEndpointsTests.cs` (10 tests, real HTTP pipeline, SQLite-backed,
  `IBookingIntentInterpreter` swapped for a fake via `WithWebHostBuilder`/`ConfigureTestServices`):
  disabled-by-default end to end, `SysAdmin` 409, oversized input 400, **a full propose -> confirm chain
  that asserts nothing exists before confirmation and a real `Confirmed` booking exists after** (#1/#2), **a
  slot that fills up between proposal and confirmation, rejected by the real capacity check** (#4), a
  `RequiresApproval` resource's confirmed booking landing `Pending` (#11), **a cross-tenant resource-name
  hint resolving to nothing, against two real seeded tenants** (#5), and a multi-candidate clarification
  naming both real resources. `BookSpace.Api.Tests/RateLimiting/AiBookingRateLimiterTests.cs` mirrors the
  existing `LoginRateLimiterTests` pattern for the new policy's own `FixedWindowRateLimiterOptions`.
- Frontend: `ai-booking.mappers.spec.ts` (wire-enum mapping). The floating assistant itself:
  `ai-assistant.service.spec.ts` (17 tests - send/Proposed/confirm end to end with the real
  `POST /api/bookings` call asserted field-for-field; never booking before explicit confirmation; a
  `Pending` approval result permanently blocking a second confirm; duplicate-confirm prevention; a 409 on
  confirm marking the proposal `failed` - not success - while leaving the conversation open to ask again;
  an older `pending` proposal becoming `superseded` and unconfirmable once a newer one is proposed;
  discarding a proposal without clearing the conversation; a Bosnian clarification round-trip's exact
  `priorTurns`; the `MAX_CONTEXT_TURNS` sliding window dropping only the OLDEST turns; a retryable
  provider-failure message and retry resending the same text; a stale `propose()`/confirm response after a
  reset being ignored; logout and a different login both clearing the conversation and closing the panel;
  and that conversation text never reaches `localStorage`/`sessionStorage`), `ai-assistant-widget.spec.ts`
  (16 tests - launcher aria-expanded/aria-controls and default-closed state; opening shows the welcome
  message and suggestion chips, which fill but never auto-send; focus moving into the composer on open and
  back to the launcher on close; Escape closing the panel; Enter sending and Shift+Enter inserting a
  newline instead; an IME composition in progress suppressing Enter; the send button disabled while a
  request is in flight; the conversation surviving a close/reopen cycle; the full proposal card
  confirm/discard flow through the rendered UI; the error bubble's Retry button; and "New conversation"
  clearing the transcript back to the welcome state), and `ask-ai.spec.ts` (the `/bookings/ask-ai` redirect
  opens the shared widget and navigates to My Bookings, rather than rendering its own page).
  `shell.spec.ts` gained two tests proving the launcher is shown for a tenant member and absent for the
  global SysAdmin.
- `BookSpace.Infrastructure.Tests/AiBooking/GeminiBookingIntentInterpreterTests.cs` (23 tests, against a
  fake `HttpMessageHandler` - never a real network call): the `x-goog-api-key` auth header (never
  `Authorization`, never in the URL); the exact `models/{model}:generateContent` request path; the
  structured-output `generationConfig` (schema shape, `nullable`, the trimmed `required` list); prior-turn
  role mapping (`assistant` -> Gemini's `model` role) and that the system instruction carries the reference
  date/time zone and resource type list; successful mapping into `LlmBookingIntent`; a blocked prompt
  (`promptFeedback.blockReason`); no candidates; an empty candidate; every non-`STOP` `finishReason`
  (`MAX_TOKENS`, `SAFETY`, `RECITATION`, `OTHER`); malformed JSON in the response text; HTTP
  400/401/403/404/429/500/503 each surfacing only the status code and Gemini's safe `status` classifier,
  never the error `message` text (asserted via a canary string that must never appear); a transport failure;
  and an already-cancelled token propagating `OperationCanceledException` rather than ever fabricating a
  successful proposal.
- Regression: the full `BookSpace.Application.Tests` (832, up from 829 after the day-wide-availability
  check added 3 tests) and `BookSpace.Api.Tests` (342, unchanged - the real-database integration tests were
  not affected by the resolution-pipeline reordering) suites, and the Angular suite (584 across 50 files, up
  from 558/48 after the floating-assistant redesign added its own files and retired `ai-booking-form.*`),
  were re-run after this feature's changes, again after the OpenAI -> Gemini migration, again after the
  floating-assistant redesign, and again after the day-wide-availability check - green throughout,
  confirming manual booking/approval behavior is unchanged. `BookSpace.Infrastructure.Tests` was also re-run
  in full
  each time (backend code did not change for the floating-assistant redesign, so only the earlier two passes
  are relevant there); see the final report for one pre-existing, zero-diff-confirmed failure found there
  (unrelated to this feature) and what was done to investigate it.
