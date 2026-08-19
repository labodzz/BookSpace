namespace BookSpace.Application.Abstractions;

public interface INotificationOutbox
{
    Task EnqueueAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}

public sealed record NotificationMessage(
    Guid TenantId,
    Guid BookingId,
    string EventType,
    DateTimeOffset OccurredAtUtc);
