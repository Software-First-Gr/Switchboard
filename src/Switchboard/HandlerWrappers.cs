using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard;

// A note on allocations, which several methods below are shaped around: C# allocates a lambda's captured
// state on entry to the method that declares the lambda, whether or not the lambda is ever created. A
// closure that must only be paid for on one branch therefore lives in a method of its own.

// --- Request wrappers (typed response) -------------------------------------

internal abstract class RequestHandlerWrapperBase
{
    public abstract Task<object?> HandleUntyped(object request, IServiceProvider provider, CancellationToken cancellationToken);
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapperBase
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly string RequestName = SwitchboardTelemetry.DisplayName(typeof(TRequest));

    public override async Task<object?> HandleUntyped(object request, IServiceProvider provider, CancellationToken cancellationToken)
        => await Handle(request, provider, cancellationToken);

    public override Task<TResponse> Handle(object request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;

        return SwitchboardTelemetry.IsRequestObserved
            ? Observed(typed, provider, cancellationToken)
            : Run(typed, provider, cancellationToken);
    }

    // Its own method so the closure is only allocated when a tracer or meter is actually listening.
    private static Task<TResponse> Observed(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
        => SwitchboardTelemetry.ObserveRequest(RequestName, () => Run(request, provider, cancellationToken));

    private static Task<TResponse> Run(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var handler = provider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var behaviors = Pipeline.Behaviors<TRequest, TResponse>(provider);

        // No behaviors: call the handler directly, without a delegate.
        return behaviors.Length == 0
            ? handler.Handle(request, cancellationToken)
            : Pipeline.Invoke(request, behaviors, 0, handler.Handle, cancellationToken);
    }
}

// --- Request wrappers (void / Unit) ----------------------------------------

internal abstract class VoidRequestHandlerWrapper
{
    public abstract Task<Unit> Handle(object request, IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class VoidRequestHandlerWrapperImpl<TRequest> : VoidRequestHandlerWrapper
    where TRequest : IRequest
{
    private static readonly string RequestName = SwitchboardTelemetry.DisplayName(typeof(TRequest));

    public override Task<Unit> Handle(object request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;

        return SwitchboardTelemetry.IsRequestObserved
            ? Observed(typed, provider, cancellationToken)
            : Run(typed, provider, cancellationToken);
    }

    private static Task<Unit> Observed(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
        => SwitchboardTelemetry.ObserveRequest(RequestName, () => Run(request, provider, cancellationToken));

    private static Task<Unit> Run(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var handler = provider.GetRequiredService<IRequestHandler<TRequest>>();
        var behaviors = Pipeline.Behaviors<TRequest, Unit>(provider);

        // Void requests run through the same pipeline with TResponse == Unit, so the existing
        // IPipelineBehavior<TRequest, Unit> registrations apply unchanged.
        return behaviors.Length == 0
            ? AsUnit(handler.Handle(request, cancellationToken))
            : Pipeline.Invoke(request, behaviors, 0, AsUnitHandler(handler), cancellationToken);
    }

    private static Func<TRequest, CancellationToken, Task<Unit>> AsUnitHandler(IRequestHandler<TRequest> handler)
        => (request, cancellationToken) => AsUnit(handler.Handle(request, cancellationToken));

    // A handler that completed synchronously costs no Task<Unit>: the cached one is returned.
    private static Task<Unit> AsUnit(Task task)
        => task.IsCompletedSuccessfully ? Unit.Task : AwaitAsUnit(task);

    private static async Task<Unit> AwaitAsUnit(Task task)
    {
        await task;
        return Unit.Value;
    }
}

// --- Behavior pipeline -----------------------------------------------------

internal static class Pipeline
{
    /// <summary>The behaviors registered for the request, in registration order: the first one runs outermost.</summary>
    public static IPipelineBehavior<TRequest, TResponse>[] Behaviors<TRequest, TResponse>(IServiceProvider provider)
    {
        var registered = provider.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        return registered as IPipelineBehavior<TRequest, TResponse>[] ?? registered.ToArray();
    }

    /// <summary>
    /// Runs <paramref name="handler"/> inside <paramref name="behaviors"/> from <paramref name="index"/> on,
    /// the first one outermost (MediatR ordering).
    /// </summary>
    /// <remarks>
    /// The token a behavior passes to <c>next</c> is what everything inside it receives, so a behavior can
    /// substitute a linked token of its own. Calling <c>next()</c> with no token (or <see langword="default"/>)
    /// keeps the token that behavior itself received, so cancellation is never lost and a substituted token
    /// survives an inner behavior that forwards nothing.
    /// </remarks>
    public static Task<TResponse> Invoke<TRequest, TResponse>(
        TRequest request,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        int index,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        CancellationToken cancellationToken)
    {
        if (index == behaviors.Length)
        {
            return handler(request, cancellationToken);
        }

        return behaviors[index].Handle(request, Next(request, behaviors, index + 1, handler, cancellationToken), cancellationToken);
    }

    private static RequestHandlerDelegate<TResponse> Next<TRequest, TResponse>(
        TRequest request,
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        int index,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        CancellationToken received)
        => token => Invoke(request, behaviors, index, handler, token == default ? received : token);
}

// --- Notification wrapper --------------------------------------------------

internal abstract class NotificationHandlerWrapper
{
    public abstract Task<Unit> Handle(object notification, IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    private static readonly string NotificationName = SwitchboardTelemetry.DisplayName(typeof(TNotification));

    public override Task<Unit> Handle(object notification, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TNotification)notification;

        return SwitchboardTelemetry.IsNotificationObserved
            ? Observed(typed, provider, cancellationToken)
            : Run(typed, provider, cancellationToken);
    }

    private static Task<Unit> Observed(TNotification notification, IServiceProvider provider, CancellationToken cancellationToken)
        => SwitchboardTelemetry.ObserveNotification(NotificationName, () => Run(notification, provider, cancellationToken));

    private static async Task<Unit> Run(TNotification notification, IServiceProvider provider, CancellationToken cancellationToken)
    {
        // Sequential dispatch: handlers run one at a time, in registration order, so they can
        // safely share scoped state (e.g. a request-scoped DbContext).
        foreach (var handler in provider.GetServices<INotificationHandler<TNotification>>())
        {
            using var activity = SwitchboardTelemetry.StartHandler(NotificationName, handler.GetType());

            try
            {
                await handler.Handle(notification, cancellationToken);
            }
            catch (Exception exception)
            {
                SwitchboardTelemetry.RecordException(activity, exception);
                throw;
            }
        }

        return Unit.Value;
    }
}
