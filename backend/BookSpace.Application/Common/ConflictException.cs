namespace BookSpace.Application.Common;

// Thrown by handlers when a request conflicts with existing state (a duplicate name, an existing
// assignment). ConflictExceptionHandler maps this to a 409 ProblemDetails; the message is written to
// be client-safe.
//
// errorCode is optional and stable (e.g. "Resource.NameConflict") so a client can branch on a
// machine-readable value instead of parsing the human-readable message. It's a later addition -
// existing throw sites that only pass a message keep compiling and simply omit the field from the
// response.
public sealed class ConflictException(string message, string? errorCode = null) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
