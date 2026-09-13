using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard;

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

        // The closure is only allocated when a tracer or meter is actually listening.
        return SwitchboardTelemetry.IsRequestObserved
            ? SwitchboardTelemetry.ObserveRequest(RequestName, () => Run(typed, provider, cancellationToken))
            : Run(typed, provider, cancellationToken);
    }

    private static Task<TResponse> Run(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var handler = provider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

        return Pipeline.Run<TRequest, TResponse>(request, provider, handler.Handle, cancellationToken);
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
            ? SwitchboardTelemetry.ObserveRequest(RequestName, () => Run(typed, provider, cancellationToken))
            : Run(typed, provider, cancellationToken);
    }

    private static Task<Unit> Run(TRequest request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var handler = provider.GetRequiredService<IRequestHandler<TRequest>>();

        // Void requests run through the same pipeline with TResponse == Unit, so the existing
        // IPipelineBehavior<TRequest, Unit> registrations apply unchanged.
        return Pipeline.Run<TRequest, Unit>(
            request,
            provider,
            async (r, ct) =>
            {
                await handler.Handle(r, ct);
                return Unit.Value;
            },
            cancellationToken);
    }
}

// --- Behavior pipeline -----------------------------------------------------

internal static class Pipeline
{
    /// <summary>
    /// Runs <paramref name="handler"/> inside the behaviors registered for the request, the first one
    /// registered outermost (MediatR ordering).
    /// </summary>
    /// <remarks>
    /// The token a behavior passes to <c>next</c> is what everything inside it receives, so a behavior can
    /// substitute a linked token of its own. Calling <c>next()</c> with no token (or <see langword="default"/>)
    /// keeps the token that behavior itself received, so cancellation is never lost and a substituted token
    /// survives an inner behavior that forwards nothing.
    /// </remarks>
    public static Task<TResponse> Run<TRequest, TResponse>(
        TRequest request,
        IServiceProvider provider,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        CancellationToken cancellationToken)
    {
        var registered = provider.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        var behaviors = registered as IPipelineBehavior<TRequest, TResponse>[] ?? registered.ToArray();

        return Invoke(request, behaviors, 0, handler, cancellationToken);
    }

    private static Task<TResponse> Invoke<TRequest, TResponse>(
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

        return behaviors[index].Handle(
            request,
            token => Invoke(request, behaviors, index + 1, handler, token == default ? cancellationToken : token),
            cancellationToken);
    }
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
            ? SwitchboardTelemetry.ObserveNotification(NotificationName, () => Run(typed, provider, cancellationToken))
            : Run(typed, provider, cancellationToken);
    }

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
