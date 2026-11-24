using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Benchmarks.Study;

// "Application" code: compiled with runtime async only in the AllRA variant. Behaviors and handlers are written the way
// applications usually write them, as async methods that await next() / their own I/O.

public sealed record DeepRequest(int Id) : IRequest<int>;

public sealed record DeepFailingRequest(int Id) : IRequest<int>;

public sealed record DeepNote(int Id) : INotification;

public sealed record DeepStream(int Count) : IStreamRequest<int>;

public sealed class DeepRequestHandler(Gate gate) : IRequestHandler<DeepRequest, int>
{
    public async Task<int> Handle(DeepRequest request, CancellationToken cancellationToken)
    {
        await gate.Wait;
        return request.Id;
    }
}

public sealed class DeepFailingRequestHandler(Gate gate) : IRequestHandler<DeepFailingRequest, int>
{
    public async Task<int> Handle(DeepFailingRequest request, CancellationToken cancellationToken)
    {
        await gate.Wait;
        throw new InvalidOperationException("handler failed");
    }
}

public sealed class DeepNoteHandler(Gate gate) : INotificationHandler<DeepNote>
{
    public async Task Handle(DeepNote notification, CancellationToken cancellationToken) => await gate.Wait;
}

public sealed class DeepStreamHandler : IStreamRequestHandler<DeepStream, int>
{
    public async IAsyncEnumerable<int> Handle(DeepStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.CompletedTask;
            yield return i;
        }
    }
}

/// <summary>A typical pass-through behavior (logging, timing, a transaction): an async method around <c>await next()</c>.</summary>
public abstract class PassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();
        return response;
    }
}

// Distinct closed types, so the same behavior registered N times is not deduplicated.
public sealed class Layer1<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer2<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer3<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer4<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer5<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer6<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer7<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;
public sealed class Layer8<TReq, TRes> : PassThroughBehavior<TReq, TRes> where TReq : notnull;

public static class StudyServices
{
    private static readonly Type[] Layers =
    [
        typeof(Layer1<,>), typeof(Layer2<,>), typeof(Layer3<,>), typeof(Layer4<,>),
        typeof(Layer5<,>), typeof(Layer6<,>), typeof(Layer7<,>), typeof(Layer8<,>),
    ];

    /// <summary>
    /// Handlers and behaviors are singletons here, so DI construction (identical in every variant) does not dilute what
    /// differs: the async frames. The Postgres load test keeps them transient, as applications do.
    /// </summary>
    public static ServiceProvider Build(Gate gate, int behaviors, bool scopePerDispatch)
    {
        var services = new ServiceCollection()
            .AddSingleton(gate)
            .AddSingleton<IRequestHandler<DeepRequest, int>, DeepRequestHandler>()
            .AddSingleton<IRequestHandler<DeepFailingRequest, int>, DeepFailingRequestHandler>()
            .AddSingleton<INotificationHandler<DeepNote>, DeepNoteHandler>()
            .AddSingleton<IStreamRequestHandler<DeepStream, int>, DeepStreamHandler>();

        for (var i = 0; i < behaviors; i++)
        {
            services.AddSingleton(typeof(IPipelineBehavior<,>), Layers[i]);
        }

        services.AddSwitchboard(cfg =>
        {
            if (scopePerDispatch)
            {
                cfg.UseScopePerDispatch();
            }
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
