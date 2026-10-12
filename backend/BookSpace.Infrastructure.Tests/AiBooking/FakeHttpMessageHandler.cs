using System.Net;
using System.Text;

namespace BookSpace.Infrastructure.Tests.AiBooking;

// A minimal stand-in for the real network transport GeminiBookingIntentInterpreterTests uses instead of
// ever making a real HTTP call - mirrors the role FakeSmtpTransport plays for SmtpNotificationSenderTests
// (see Email/FakeSmtpTransport.cs). Captures the single outgoing request (this interpreter only ever
// sends one per InterpretAsync call - see its own "no retries inside the interpreter" doc comment) so a
// test can assert on its headers/body, and lets each test script exactly the response (or failure) it
// wants to exercise.
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public static FakeHttpMessageHandler ReturningJson(HttpStatusCode statusCode, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, cancellationToken);
    }
}
