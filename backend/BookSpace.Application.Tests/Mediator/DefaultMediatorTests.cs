using BookSpace.Application.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookSpace.Application.Tests.Mediator;

public sealed class DefaultMediatorTests
{
    private sealed record EchoRequest(string Value) : IRequest<string>;

    private sealed class EchoRequestHandler : IRequestHandler<EchoRequest, string>
    {
        public Task<string> Handle(EchoRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
    }

    private sealed class CallLog
    {
        public List<string> Entries { get; } = [];
    }

    private sealed class FirstBehavior<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            log.Entries.Add("first:before");
            var response = await next();
            log.Entries.Add("first:after");
            return response;
        }
    }

    private sealed class SecondBehavior<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            log.Entries.Add("second:before");
            var response = await next();
            log.Entries.Add("second:after");
            return response;
        }
    }

    [Fact]
    public async Task Send_WithoutRegisteredBehaviors_DispatchesDirectlyToHandler()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMediator, DefaultMediator>();
        services.AddScoped<IRequestHandler<EchoRequest, string>, EchoRequestHandler>();
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        var result = await mediator.Send(new EchoRequest("hello"), CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task Send_WithMultipleBehaviors_RunsThemInRegistrationOrderAroundTheHandler()
    {
        var log = new CallLog();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<IMediator, DefaultMediator>();
        services.AddScoped<IRequestHandler<EchoRequest, string>, EchoRequestHandler>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(SecondBehavior<,>));
        var mediator = services.BuildServiceProvider().GetRequiredService<IMediator>();

        var result = await mediator.Send(new EchoRequest("hello"), CancellationToken.None);

        Assert.Equal("hello", result);
        Assert.Equal(["first:before", "second:before", "second:after", "first:after"], log.Entries);
    }
}
