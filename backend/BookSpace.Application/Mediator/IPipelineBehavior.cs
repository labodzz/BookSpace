namespace BookSpace.Application.Mediator;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

// Behaviors registered earlier run outermost - e.g. logging registered before validation sees the
// request first and can still log the fact that validation short-circuited it.
public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
