namespace BookSpace.Application.Logging;

public interface ICorrelationIdContext
{
    string? CorrelationId { get; }

    void Set(string correlationId);
}
