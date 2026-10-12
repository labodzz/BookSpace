using System.Net;
using System.Text.Json;
using BookSpace.Application.AiBooking;
using BookSpace.Infrastructure.AiBooking;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.AiBooking;

// Exercises GeminiBookingIntentInterpreter's own request-building/response-parsing/error-handling logic
// against a fake HttpMessageHandler - never a real network call, matching the task's own "CI must not
// call a real paid LLM API" requirement (see FakeBookingIntentInterpreter for the layer above this one
// that every other AiBooking test in this codebase uses instead). These tests prove the BACKEND correctly
// handles a given wire-level Gemini response shape; they prove nothing about whether a real Gemini model
// would actually produce a good one for a given sentence - see docs/ai-assisted-booking.md ("What was
// verified vs. what was NOT").
public sealed class GeminiBookingIntentInterpreterTests
{
    private static readonly BookingIntentContext Context = new(
        ["Lab", "Meeting Room"], new DateOnly(2026, 10, 8), new TimeOnly(10, 0), "Europe/Sarajevo", DayOfWeek.Thursday);

    private const string ValidIntentJson = """
        {
          "language": "en",
          "status": "ok",
          "clarificationQuestion": null,
          "resourceTypeHint": "Lab",
          "resourceNameHint": null,
          "partySize": 3,
          "quantity": null,
          "date": "2026-10-13",
          "dateIsRelativeExpression": true,
          "startTime": "14:00",
          "endTime": "16:00",
          "durationMinutes": null
        }
        """;

    private static string CandidateResponse(string text, string? finishReason = "STOP") => JsonSerializer.Serialize(new
    {
        candidates = new[]
        {
            new
            {
                content = new { parts = new[] { new { text } }, role = "model" },
                finishReason,
            },
        },
    });

    private static (GeminiBookingIntentInterpreter Interpreter, FakeHttpMessageHandler Handler) CreateInterpreter(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, AiBookingOptions? options = null)
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        var interpreter = new GeminiBookingIntentInterpreter(
            httpClient, Options.Create(options ?? new AiBookingOptions { Enabled = true, ApiKey = "test-key", Model = "gemini-3.5-flash-lite" }));
        return (interpreter, handler);
    }

    private static (GeminiBookingIntentInterpreter Interpreter, FakeHttpMessageHandler Handler) CreateInterpreterReturning(
        HttpStatusCode statusCode, string json, AiBookingOptions? options = null)
    {
        var handler = FakeHttpMessageHandler.ReturningJson(statusCode, json);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        var interpreter = new GeminiBookingIntentInterpreter(
            httpClient, Options.Create(options ?? new AiBookingOptions { Enabled = true, ApiKey = "test-key", Model = "gemini-3.5-flash-lite" }));
        return (interpreter, handler);
    }

    [Fact]
    public async Task InterpretAsync_SendsTheApiKeyAsAGoogHeaderNeverAsAuthorizationOrInTheUrl()
    {
        var (interpreter, handler) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson));

        await interpreter.InterpretAsync("test", [], Context, CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("x-goog-api-key", out var values));
        Assert.Equal("test-key", Assert.Single(values!));
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.DoesNotContain("test-key", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task InterpretAsync_PostsToTheModelsGenerateContentPathForTheConfiguredModel()
    {
        var (interpreter, handler) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson));

        await interpreter.InterpretAsync("test", [], Context, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent",
            handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task InterpretAsync_RequestsStructuredJsonOutputWithAGeminiCompatibleSchema()
    {
        var (interpreter, handler) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson));

        await interpreter.InterpretAsync("test", [], Context, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var generationConfig = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", generationConfig.GetProperty("responseMimeType").GetString());

        var schema = generationConfig.GetProperty("responseSchema");
        // Gemini's Type enum is uppercase (OBJECT/STRING/...), not lowercase JSON-Schema-style - a real
        // HTTP 400 INVALID_ARGUMENT against the live API proved this the hard way.
        Assert.Equal("OBJECT", schema.GetProperty("type").GetString());
        // Gemini's schema dialect: optionality via "nullable", never an OpenAI-style ["string","null"]
        // type array.
        var dateProperty = schema.GetProperty("properties").GetProperty("date");
        Assert.Equal("STRING", dateProperty.GetProperty("type").GetString());
        Assert.True(dateProperty.GetProperty("nullable").GetBoolean());
        // Only the fields the model always fills in are required - everything else may be left out.
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["language", "status"], required);
    }

    [Fact]
    public async Task InterpretAsync_MapsPriorTurnsToGeminiRolesAndIncludesTheReferenceContextInTheSystemInstruction()
    {
        var (interpreter, handler) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson));
        var priorTurns = new List<BookingIntentTurn>
        {
            new(BookingIntentTurnRole.User, "Treba mi laboratorija"),
            new(BookingIntentTurnRole.Assistant, "Which date?"),
        };

        await interpreter.InterpretAsync("sljedeci utorak", priorTurns, Context, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var contents = body.RootElement.GetProperty("contents").EnumerateArray().ToList();
        Assert.Equal(3, contents.Count);
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("Treba mi laboratorija", contents[0].GetProperty("parts")[0].GetProperty("text").GetString());
        // The assistant's prior turn must map to Gemini's "model" role, never "assistant".
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
        Assert.Equal("sljedeci utorak", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());

        var systemInstructionText = body.RootElement
            .GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString();
        Assert.Contains("Lab, Meeting Room", systemInstructionText);
        Assert.Contains("2026-10-08", systemInstructionText);
        Assert.Contains("Europe/Sarajevo", systemInstructionText);
        Assert.Contains("Bosnian or English", systemInstructionText);
    }

    [Fact]
    public async Task InterpretAsync_WithASuccessfulResponse_MapsItIntoTheSharedLlmBookingIntentContract()
    {
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson));

        var intent = await interpreter.InterpretAsync("book a lab", [], Context, CancellationToken.None);

        Assert.Equal("en", intent.Language);
        Assert.Equal("ok", intent.Status);
        Assert.Equal("Lab", intent.ResourceTypeHint);
        Assert.Equal(3, intent.PartySize);
        Assert.Equal("2026-10-13", intent.Date);
        Assert.Equal("14:00", intent.StartTime);
        Assert.Equal("16:00", intent.EndTime);
    }

    [Fact]
    public async Task InterpretAsync_WhenTheProviderReturnsMalformedJsonInTheTextPart_ThrowsInterpretationException()
    {
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse("{not valid json"));

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("could not be parsed", exception.Message);
    }

    [Fact]
    public async Task InterpretAsync_WhenThePromptWasBlocked_ThrowsInterpretationExceptionNamingTheBlockReason()
    {
        var responseJson = JsonSerializer.Serialize(new { promptFeedback = new { blockReason = "SAFETY" } });
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, responseJson);

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("blocked", exception.Message);
        Assert.Contains("SAFETY", exception.Message);
    }

    [Fact]
    public async Task InterpretAsync_WhenThereAreNoCandidatesAndNoBlockReason_ThrowsInterpretationException()
    {
        var responseJson = JsonSerializer.Serialize(new { candidates = Array.Empty<object>() });
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, responseJson);

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("no candidates", exception.Message);
    }

    [Fact]
    public async Task InterpretAsync_WhenTheCandidateHasNoText_ThrowsInterpretationException()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = Array.Empty<object>() }, finishReason = "STOP" } },
        });
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, responseJson);

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("empty response", exception.Message);
    }

    [Theory]
    [InlineData("MAX_TOKENS")]
    [InlineData("SAFETY")]
    [InlineData("RECITATION")]
    [InlineData("OTHER")]
    public async Task InterpretAsync_WhenFinishReasonIsNotStop_ThrowsInterpretationExceptionNamingIt(string finishReason)
    {
        // Even if the (possibly truncated) text happens to still contain validly-shaped JSON, a non-STOP
        // finish reason must still be rejected - it is never trusted as a complete, safe response.
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.OK, CandidateResponse(ValidIntentJson, finishReason));

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains(finishReason, exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_ARGUMENT")]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHENTICATED")]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED")]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED")]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE")]
    public async Task InterpretAsync_OnANonSuccessStatus_ThrowsWithTheStatusCodeAndSafeClassifierOnly(
        HttpStatusCode statusCode, string status)
    {
        const string canarySecretText = "do-not-leak-this-message-text";
        var errorJson = JsonSerializer.Serialize(new { error = new { code = (int)statusCode, message = canarySecretText, status } });
        var (interpreter, _) = CreateInterpreterReturning(statusCode, errorJson);

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains(((int)statusCode).ToString(), exception.Message);
        Assert.Contains(status, exception.Message);
        Assert.DoesNotContain(canarySecretText, exception.Message);
    }

    [Fact]
    public async Task InterpretAsync_OnANonSuccessStatusWithAnUnparsableBody_StillThrowsWithJustTheStatusCode()
    {
        var (interpreter, _) = CreateInterpreterReturning(HttpStatusCode.BadGateway, "not json at all");

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("502", exception.Message);
    }

    [Fact]
    public async Task InterpretAsync_OnATransportFailure_ThrowsInterpretationException()
    {
        var (interpreter, _) = CreateInterpreter((_, _) => throw new HttpRequestException("connection refused"));

        var exception = await Assert.ThrowsAsync<BookingIntentInterpretationException>(
            () => interpreter.InterpretAsync("test", [], Context, CancellationToken.None));
        Assert.Contains("network", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InterpretAsync_RespectsAnAlreadyCancelledToken_NeverFabricatingASuccessfulProposal()
    {
        var (interpreter, _) = CreateInterpreter(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interpreter.InterpretAsync("test", [], Context, cts.Token));
    }
}
