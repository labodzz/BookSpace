namespace BookSpace.Application.Common;

// Thrown by handlers when a requested entity doesn't exist (or - thanks to the tenant query filter -
// belongs to a different tenant and is therefore invisible, which correctly looks identical to "not
// found" rather than leaking a 403 that would confirm another tenant's data exists).
// NotFoundExceptionHandler maps this to a 404 ProblemDetails; the message is written to be client-safe.
//
// errorCode is optional and stable (e.g. "Resource.NotFound") so a client can branch on a machine-
// readable value instead of parsing the human-readable message. It's a later addition - existing
// throw sites that only pass a message keep compiling and simply omit the field from the response.
public sealed class NotFoundException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
