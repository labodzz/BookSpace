using BookSpace.Application.Bookings;

namespace BookSpace.Application.Tests.Bookings;

// Shared no-op IResourceBookingLock for handler tests (no real DB/transaction here) - concurrency itself
// is proven separately against real SQL Server in BookSpace.Infrastructure.Tests. Used by any handler
// test whose handler takes IResourceBookingLock as a dependency (CreateBookingCommandHandlerTests,
// UpdateResourceCommandHandlerTests, ...).
public sealed class PassThroughResourceBookingLock : IResourceBookingLock
{
    public Task<TResult> RunExclusiveAsync<TResult>(
        Guid resourceId, Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
        operation(cancellationToken);
}
