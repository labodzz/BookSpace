namespace BookSpace.Application.Common;

// Thrown by handlers when a requested entity doesn't exist (or - thanks to the tenant query filter -
// belongs to a different tenant and is therefore invisible, which correctly looks identical to "not
// found" rather than leaking a 403 that would confirm another tenant's data exists).
// NotFoundExceptionHandler maps this to a 404 ProblemDetails; the message is written to be client-safe.
public sealed class NotFoundException(string message) : Exception(message);
