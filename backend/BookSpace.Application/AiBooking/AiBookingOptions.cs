namespace BookSpace.Application.AiBooking;

// Bound from the "AiBooking" configuration section - see docs/ai-assisted-booking.md. Disabled by
// default, same reasoning as EmailOptions.Enabled/BackgroundJobsOptions.Enabled: an unconfigured
// deployment (including every existing environment this codebase already runs in - Development,
// Testing, and any container/Azure deployment that predates this feature) must start and operate
// normally with no LLM provider configured at all, never fail startup or silently try to call out to
// one. ApiKey is never read from any tracked appsettings*.json file - only from user-secrets locally or
// the AiBooking__ApiKey environment variable in a container/Azure (see AiBookingOptionsValidator and
// docs/ai-assisted-booking.md, "Configuration and secrets") - and is never logged, never returned in any
// API response, and never sent to the Angular client.
public sealed class AiBookingOptions
{
    public bool Enabled { get; init; }

    // Holds a Gemini API key (see https://aistudio.google.com/apikey) - never an OpenAI key. The name is
    // kept provider-agnostic (not "GeminiApiKey") because this codebase only ever configures one LLM
    // provider at a time - see docs/ai-assisted-booking.md ("Configuration") for the full rationale.
    public string ApiKey { get; init; } = string.Empty;

    // "Current, small, cost-effective, Free-Tier-eligible, structured-output-capable Gemini model" at the
    // time this was written (2026-10, see docs/ai-assisted-booking.md for the full justification and a
    // link to check before relying on this long-term - provider model lineups and free-tier eligibility
    // change over time). Configurable rather than hardcoded into GeminiBookingIntentInterpreter so a model
    // swap never needs a code change.
    public string Model { get; init; } = "gemini-3.5-flash-lite";

    // The Gemini generateContent API base - overridable so a compatible proxy endpoint (or a future
    // provider swap) never needs a code change, and so tests never need a real network address.
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta/";

    // Bounds how long ProposeBookingCommandHandler waits for a single interpretation call before giving
    // up and reporting ProviderUnavailable - see the "Limit ... request duration" requirement in
    // docs/ai-assisted-booking.md. No retry loop: a timed-out or failed call is reported once, never
    // retried automatically (an unbounded/automatic retry against a paid, latency-sensitive external API
    // is exactly what this bound exists to prevent).
    public int RequestTimeoutSeconds { get; init; } = 15;

    // A hard ceiling on the model's own output size (Gemini's generationConfig.maxOutputTokens) - bounds
    // cost and latency per call independent of RequestTimeoutSeconds, which only bounds wall-clock wait
    // time.
    public int MaxOutputTokens { get; init; } = 400;
}
