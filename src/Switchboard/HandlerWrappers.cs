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
    public override async Task<object?> HandleUntyped(object request, IServiceProvider provider, CancellationToken cancellationToken)
        => await Handle(request, provider, cancellationToken);

    public override Task<TResponse> Handle(object request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;
        var handler = provider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

        // Every delegate ignores its own token argument and uses the original one captured
        // from Send, so cancellation propagates even when a behavior calls next() with no args.
        RequestHandlerDelegate<TResponse> next = _ => handler.Handle(typed, cancellationToken);

        // Reverse so the first-registered behavior ends up outermost (matches MediatR ordering).
        foreach (var behavior in provider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var behaviorLocal = behavior;
            var nextLocal = next;
            next = _ => behaviorLocal.Handle(typed, nextLocal, cancellationToken);
        }

        return next(cancellationToken);
    }
}

// --- Request wrappers (void / Unit) ----------------------------------------

internal abstract class VoidRequestHandlerWrapper
{
    public abstract Task Handle(object request, IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class VoidRequestHandlerWrapperImpl<TRequest> : VoidRequestHandlerWrapper
    where TRequest : IRequest
{
    public override Task Handle(object request, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TRequest)request;
        var handler = provider.GetRequiredService<IRequestHandler<TRequest>>();

        // Void requests run through the same pipeline with TResponse == Unit, so the existing
        // IPipelineBehavior<TRequest, Unit> registrations apply unchanged.
        RequestHandlerDelegate<Unit> next = async _ =>
        {
            await handler.Handle(typed, cancellationToken);
            return Unit.Value;
        };

        foreach (var behavior in provider.GetServices<IPipelineBehavior<TRequest, Unit>>().Reverse())
        {
            var behaviorLocal = behavior;
            var nextLocal = next;
            next = _ => behaviorLocal.Handle(typed, nextLocal, cancellationToken);
        }

        return next(cancellationToken);
    }
}

// --- Notification wrapper --------------------------------------------------

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(object notification, IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override async Task Handle(object notification, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var typed = (TNotification)notification;

        // Sequential dispatch: handlers run one at a time, in registration order, so they can
        // safely share scoped state (e.g. a request-scoped DbContext).
        foreach (var handler in provider.GetServices<INotificationHandler<TNotification>>())
        {
            await handler.Handle(typed, cancellationToken);
        }
    }
}
