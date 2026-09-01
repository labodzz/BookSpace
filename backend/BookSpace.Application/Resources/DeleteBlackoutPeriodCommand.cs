using BookSpace.Application.Common;
using BookSpace.Application.Mediator;

namespace BookSpace.Application.Resources;

public sealed record DeleteBlackoutPeriodCommand(Guid ResourceId, Guid BlackoutId) : IRequest<Unit>;

public sealed class DeleteBlackoutPeriodCommandHandler(IBlackoutPeriodRepository blackoutPeriodRepository)
    : IRequestHandler<DeleteBlackoutPeriodCommand, Unit>
{
    public async Task<Unit> Handle(DeleteBlackoutPeriodCommand request, CancellationToken cancellationToken)
    {
        var period = await blackoutPeriodRepository.FindByIdAsync(request.BlackoutId, cancellationToken);
        if (period is null || period.ResourceId != request.ResourceId)
        {
            throw new NotFoundException(
                $"Blackout period {request.BlackoutId} was not found for resource {request.ResourceId}.",
                ErrorCodes.BlackoutPeriodNotFound);
        }

        await blackoutPeriodRepository.RemoveAsync(period, cancellationToken);
        await blackoutPeriodRepository.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
