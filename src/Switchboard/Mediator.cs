using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard;

/// <summary>
/// Default <see cref="IMediator"/> implementation. Resolves handlers and pipeline
/// behaviors from the current <see cref="IServiceProvider"/> scope and composes them
/// the same way MediatR does: behaviors execute in registration order, outermost first.
/// </summary>
public sealed class Mediator : IMediator
{
    private readonly IServiceProvider _provider;
    private readonly DispatchScopeOptions? _scopePerDispatch;

    // Closed wrappers are cached per request/notification type; they are stateless and thread-safe.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapperBase> RequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, VoidRequestHandlerWrapper> VoidRequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationWrappers = new();

    // The scope of the dispatch in flight on this async path, when scope-per-dispatch is on. A handler
    // that sends or publishes again reuses it, so the inner work shares the outer unit of work.
    private static readonly AsyncLocal<IServiceProvider?> ActiveDispatchScope = new();

    /// <summary>Creates a mediator that resolves handlers and behaviors from <paramref name="provider"/>.</summary>
    public Mediator(IServiceProvider provider)
    {
        _provider = provider;
        _scopePerDispatch = provider.GetService<DispatchScopeOptions>();
    }

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)RequestWrappers.GetOrAdd(
            request.GetType(),
            requestType => (RequestHandlerWrapperBase)Activator.CreateInstance(
                typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestType, typeof(TResponse)))!);

        return Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
    }

    /// <inheritdoc />
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = VoidRequestWrappers.GetOrAdd(
            request.GetType(),
            static requestType => (VoidRequestHandlerWrapper)Activator.CreateInstance(
                typeof(VoidRequestHandlerWrapperImpl<>).MakeGenericType(requestType))!);

        return Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
    }

    /// <inheritdoc />
    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var responseInterface = Array.Find(
            requestType.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        if (responseInterface is not null)
        {
            var responseType = responseInterface.GetGenericArguments()[0];
            var wrapper = RequestWrappers.GetOrAdd(
                requestType,
                rt => (RequestHandlerWrapperBase)Activator.CreateInstance(
                    typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(rt, responseType))!);

            return await Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.HandleUntyped(r, sp, ct));
        }

        if (request is IRequest)
        {
            var wrapper = VoidRequestWrappers.GetOrAdd(
                requestType,
                static rt => (VoidRequestHandlerWrapper)Activator.CreateInstance(
                    typeof(VoidRequestHandlerWrapperImpl<>).MakeGenericType(rt))!);

            await Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
            return null;
        }

        throw new ArgumentException(
            $"{requestType} does not implement {nameof(IRequest)} or {typeof(IRequest<>).Name}", nameof(request));
    }

    /// <inheritdoc />
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification)
        {
            throw new ArgumentException(
                $"{notification.GetType()} does not implement {nameof(INotification)}", nameof(notification));
        }

        return PublishInternal(notification, cancellationToken);
    }

    /// <inheritdoc />
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        return PublishInternal(notification, cancellationToken);
    }

    private Task PublishInternal(object notification, CancellationToken cancellationToken)
    {
        var wrapper = NotificationWrappers.GetOrAdd(
            notification.GetType(),
            static notificationType => (NotificationHandlerWrapper)Activator.CreateInstance(
                typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(notificationType))!);

        return Dispatch(wrapper, notification, cancellationToken, static (w, n, sp, ct) => w.Handle(n, sp, ct));
    }

    /// <summary>
    /// Picks the provider the message is handled from: the caller's scope by default, or — with
    /// scope-per-dispatch on — the dispatch already in flight, else a fresh scope for this message.
    /// </summary>
    private Task<TResult> Dispatch<TWrapper, TResult>(
        TWrapper wrapper,
        object message,
        CancellationToken cancellationToken,
        Func<TWrapper, object, IServiceProvider, CancellationToken, Task<TResult>> invoke)
    {
        if (_scopePerDispatch is null)
        {
            return invoke(wrapper, message, _provider, cancellationToken);
        }

        if (ActiveDispatchScope.Value is { } activeScope)
        {
            return invoke(wrapper, message, activeScope, cancellationToken);
        }

        return DispatchInNewScope(_scopePerDispatch, wrapper, message, cancellationToken, invoke);
    }

    private async Task<TResult> DispatchInNewScope<TWrapper, TResult>(
        DispatchScopeOptions options,
        TWrapper wrapper,
        object message,
        CancellationToken cancellationToken,
        Func<TWrapper, object, IServiceProvider, CancellationToken, Task<TResult>> invoke)
    {
        await using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

        // Set inside an async method, so the value flows into the handler and anything it awaits,
        // but never back out to the caller: sibling dispatches each still get their own scope.
        ActiveDispatchScope.Value = scope.ServiceProvider;

        if (options.OnScopeCreated is { } onScopeCreated)
        {
            await onScopeCreated(new DispatchScope(_provider, scope.ServiceProvider, message), cancellationToken);
        }

        return await invoke(wrapper, message, scope.ServiceProvider, cancellationToken);
    }
}
