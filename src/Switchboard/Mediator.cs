using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    // A typed request's wrapper is built for the response type the request declares through IRequest<TResponse>,
    // never for the type argument of one particular Send call; null is cached for a type that declares none.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapperBase?> RequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, VoidRequestHandlerWrapper> VoidRequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationWrappers = new();
    private static readonly ConcurrentDictionary<Type, StreamRequestHandlerWrapper?> StreamWrappers = new();

    // The scope of the dispatch in flight on this async path, when scope-per-dispatch is on. A handler
    // that sends or publishes again through a mediator resolved in that scope reuses it, so the inner
    // work shares the outer unit of work.
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

        // Never null here: the static type guarantees the runtime type declares an IRequest<T>.
        var wrapper = TypedRequestWrapper(request.GetType())!;

        if (wrapper is RequestHandlerWrapper<TResponse> exact)
        {
            return Dispatch(exact, request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
        }

        // IRequest<out TResponse> is covariant, so GetOrder : IRequest<OrderDto> can be sent as IRequest<object>.
        // The handler is still the one registered for OrderDto; only the response is converted. Building the
        // wrapper from the call's type argument instead would look for IRequestHandler<GetOrder, object> and,
        // worse, cache that wrapper for every later Send of GetOrder.
        return SendCovariant<TResponse>(wrapper, request, cancellationToken);
    }

    /// <inheritdoc />
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        return Dispatch(VoidRequestWrapper(request.GetType()), request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
    }

    /// <inheritdoc />
    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is IBaseRequest && TypedRequestWrapper(request.GetType()) is { } wrapper)
        {
            return await Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.HandleUntyped(r, sp, ct));
        }

        if (request is IRequest)
        {
            await Dispatch(VoidRequestWrapper(request.GetType()), request, cancellationToken, static (w, r, sp, ct) => w.Handle(r, sp, ct));
            return null;
        }

        throw new ArgumentException(
            $"{request.GetType()} does not implement {nameof(IRequest)} or {typeof(IRequest<>).Name}", nameof(request));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Never null here: the static type guarantees the runtime type declares an IStreamRequest<T>. The wrapper is the
        // one for the declared item type; IStreamRequest<out T> and IAsyncEnumerable<out T> are both covariant, so a
        // stream of OrderDto created through IStreamRequest<object> is already a stream of object, with no conversion.
        var wrapper = StreamWrapper(request.GetType())!;

        return Stream(wrapper, request, cancellationToken, static (w, r, sp, ct) => (IAsyncEnumerable<TResponse>)w.Handle(r, sp, ct));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = StreamWrapper(request.GetType()) ?? throw new ArgumentException(
            $"{request.GetType()} does not implement {typeof(IStreamRequest<>).Name}", nameof(request));

        return Stream(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.HandleUntyped(r, sp, ct));
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

    private async Task<TResponse> SendCovariant<TResponse>(RequestHandlerWrapperBase wrapper, object request, CancellationToken cancellationToken)
        => (TResponse)(await Dispatch(wrapper, request, cancellationToken, static (w, r, sp, ct) => w.HandleUntyped(r, sp, ct)))!;

    /// <summary>The wrapper for the response type <paramref name="requestType"/> declares, or <see langword="null"/> when it declares none.</summary>
    private static RequestHandlerWrapperBase? TypedRequestWrapper(Type requestType)
        => RequestWrappers.GetOrAdd(requestType, static type =>
        {
            var declared = Array.Find(
                type.GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

            return declared is null
                ? null
                : (RequestHandlerWrapperBase)Activator.CreateInstance(
                    typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(type, declared.GetGenericArguments()[0]))!;
        });

    /// <summary>The wrapper for the item type <paramref name="requestType"/> declares, or <see langword="null"/> when it declares none.</summary>
    private static StreamRequestHandlerWrapper? StreamWrapper(Type requestType)
        => StreamWrappers.GetOrAdd(requestType, static type =>
        {
            var declared = Array.Find(
                type.GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>));

            return declared is null
                ? null
                : (StreamRequestHandlerWrapper)Activator.CreateInstance(
                    typeof(StreamRequestHandlerWrapperImpl<,>).MakeGenericType(type, declared.GetGenericArguments()[0]))!;
        });

    private static VoidRequestHandlerWrapper VoidRequestWrapper(Type requestType)
        => VoidRequestWrappers.GetOrAdd(requestType, static type => (VoidRequestHandlerWrapper)Activator.CreateInstance(
            typeof(VoidRequestHandlerWrapperImpl<>).MakeGenericType(type))!);

    /// <summary>
    /// Picks the provider the message is handled from: the caller's scope by default, or — with
    /// scope-per-dispatch on — the dispatch in flight when this mediator was resolved from it, else a
    /// fresh scope for this message.
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

        // Only a mediator resolved from the scope in flight — the ISender or IPublisher injected into a
        // handler — joins it. One resolved from a scope the caller created on purpose, to keep its own
        // DbContext, must not be folded back into the outer unit of work.
        if (ReferenceEquals(ActiveDispatchScope.Value, _provider))
        {
            return invoke(wrapper, message, _provider, cancellationToken);
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

    private IAsyncEnumerable<TItem> Stream<TItem>(
        StreamRequestHandlerWrapper wrapper,
        object request,
        CancellationToken cancellationToken,
        Func<StreamRequestHandlerWrapper, object, IServiceProvider, CancellationToken, IAsyncEnumerable<TItem>> open)
        => new DispatchedStream<TItem>(this, wrapper, request, cancellationToken, open);

    /// <summary>
    /// The streaming counterpart of <see cref="Dispatch"/>: picks the provider the same way, but a new scope lives
    /// for the whole enumeration rather than for one call.
    /// </summary>
    private IAsyncEnumerable<TItem> OpenStream<TItem>(
        StreamRequestHandlerWrapper wrapper,
        object request,
        CancellationToken cancellationToken,
        Func<StreamRequestHandlerWrapper, object, IServiceProvider, CancellationToken, IAsyncEnumerable<TItem>> open)
    {
        if (_scopePerDispatch is null || ReferenceEquals(ActiveDispatchScope.Value, _provider))
        {
            return open(wrapper, request, _provider, cancellationToken);
        }

        return StreamInNewScope(_scopePerDispatch, wrapper, request, cancellationToken, open);
    }

    // The scope is created by the first MoveNextAsync and disposed when the stream ends, throws, or is disposed early
    // (a consumer that breaks out of await foreach). A stream that is never enumerated never creates one.
    private async IAsyncEnumerable<TItem> StreamInNewScope<TItem>(
        DispatchScopeOptions options,
        StreamRequestHandlerWrapper wrapper,
        object request,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Func<StreamRequestHandlerWrapper, object, IServiceProvider, CancellationToken, IAsyncEnumerable<TItem>> open)
    {
        await using var scope = _provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

        ActiveDispatchScope.Value = scope.ServiceProvider;

        if (options.OnScopeCreated is { } onScopeCreated)
        {
            await onScopeCreated(new DispatchScope(_provider, scope.ServiceProvider, request), cancellationToken);
        }

        await using var items = open(wrapper, request, scope.ServiceProvider, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            // An iterator resumes on its consumer's execution context after every yield, which does not carry the
            // scope, so it is set again before each step for the handler's own sends to join it.
            ActiveDispatchScope.Value = scope.ServiceProvider;

            if (!await items.MoveNextAsync())
            {
                yield break;
            }

            yield return items.Current;
        }
    }

    // What CreateStream returns. Every enumeration dispatches the request afresh, from a new iterator: a C# iterator
    // hands its own instance out again once it has finished on the thread that created it, even before it is
    // disposed, so returning one directly would let an overlapping enumeration take over a finished one's state
    // machine, yielding nothing or failing when the first consumer disposes it.
    private sealed class DispatchedStream<TItem> : IAsyncEnumerable<TItem>
    {
        private readonly Mediator _mediator;
        private readonly StreamRequestHandlerWrapper _wrapper;
        private readonly object _request;
        private readonly CancellationToken _cancellationToken;
        private readonly Func<StreamRequestHandlerWrapper, object, IServiceProvider, CancellationToken, IAsyncEnumerable<TItem>> _open;

        public DispatchedStream(
            Mediator mediator,
            StreamRequestHandlerWrapper wrapper,
            object request,
            CancellationToken cancellationToken,
            Func<StreamRequestHandlerWrapper, object, IServiceProvider, CancellationToken, IAsyncEnumerable<TItem>> open)
        {
            _mediator = mediator;
            _wrapper = wrapper;
            _request = request;
            _cancellationToken = cancellationToken;
            _open = open;
        }

        public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => _mediator.OpenStream(_wrapper, _request, _cancellationToken, _open).GetAsyncEnumerator(cancellationToken);
    }
}
