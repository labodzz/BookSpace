using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.Application.Mediator;

// Send&lt;TResponse&gt;(IRequest&lt;TResponse&gt;) only knows the request's concrete type at runtime, but
// resolving IRequestHandler&lt;TRequest,TResponse&gt; from DI needs that type as a generic argument known
// at compile time. A wrapper closes over both type arguments once per request type (cached by
// Mediator) so the actual dispatch - resolving the handler, building the behavior chain, invoking it -
// is fully typed and reflection-free.
internal abstract class RequestHandlerBase<TResponse>
{
    public abstract Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerBase<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

        RequestHandlerDelegate<TResponse> pipeline = () => handler.Handle(typedRequest, cancellationToken);

        foreach (var behavior in behaviors.Reverse())
        {
            var next = pipeline;
            pipeline = () => behavior.Handle(typedRequest, next, cancellationToken);
        }

        return pipeline();
    }
}
