using System.Collections.Concurrent;

namespace BookSpace.Application.Mediator;

public sealed class DefaultMediator(IServiceProvider serviceProvider) : IMediator
{
    private static readonly ConcurrentDictionary<Type, object> WrapperCache = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        var wrapper = (RequestHandlerBase<TResponse>)WrapperCache.GetOrAdd(requestType, static rt =>
            Activator.CreateInstance(typeof(RequestHandlerWrapper<,>).MakeGenericType(rt, typeof(TResponse)))!);

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }
}
