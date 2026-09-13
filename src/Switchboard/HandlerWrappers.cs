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

        // Every delegate ignores its own token argument and uses the original one captured
        // from Send, so cancellation propagates even when a behavior calls next() with no args.
        RequestHandlerDelegate<TResponse> next = _ => handler.Handle(request, cancellationToken);

        // Reverse so the first-registered behavior ends up outermost (matches MediatR ordering).
        foreach (var behavior in provider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var behaviorLocal = behavior;
            var nextLocal = next;
            next = _ => behaviorLocal.Handle(request, nextLocal, cancellationToken);
        }

        return next(cancellationToken);
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
        RequestHandlerDelegate<Unit> next = async _ =>
        {
            await handler.Handle(request, cancellationToken);
            return Unit.Value;
        };

        foreach (var behavior in provider.GetServices<IPipelineBehavior<TRequest, Unit>>().Reverse())
        {
            var behaviorLocal = behavior;
            var nextLocal = next;
            next = _ => behaviorLocal.Handle(request, nextLocal, cancellationToken);
        }

        return next(cancellationToken);
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
