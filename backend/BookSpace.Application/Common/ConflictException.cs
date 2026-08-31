namespace BookSpace.Application.Common;

// Thrown by handlers when a request conflicts with existing state (a duplicate name, an existing
// assignment). ConflictExceptionHandler maps this to a 409 ProblemDetails; the message is written to
// be client-safe.
public sealed class ConflictException(string message) : Exception(message);
