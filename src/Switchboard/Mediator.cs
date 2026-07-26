using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>
/// Default <see cref="IMediator"/> implementation. Resolves handlers and pipeline
/// behaviors from the current <see cref="IServiceProvider"/> scope and composes them
/// the same way MediatR does: behaviors execute in registration order, outermost first.
/// </summary>
public sealed class Mediator : IMediator
{
    private readonly IServiceProvider _provider;

    // Closed wrappers are cached per request/notification type; they are stateless and thread-safe.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapperBase> RequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, VoidRequestHandlerWrapper> VoidRequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationWrappers = new();

    /// <summary>Creates a mediator that resolves handlers and behaviors from <paramref name="provider"/>.</summary>
    public Mediator(IServiceProvider provider) => _provider = provider;

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)RequestWrappers.GetOrAdd(
            request.GetType(),
            requestType => (RequestHandlerWrapperBase)Activator.CreateInstance(
                typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestType, typeof(TResponse)))!);

        return wrapper.Handle(request, _provider, cancellationToken);
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

        return wrapper.Handle(request, _provider, cancellationToken);
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

            return await wrapper.HandleUntyped(request, _provider, cancellationToken);
        }

        if (request is IRequest)
        {
            var wrapper = VoidRequestWrappers.GetOrAdd(
                requestType,
                static rt => (VoidRequestHandlerWrapper)Activator.CreateInstance(
                    typeof(VoidRequestHandlerWrapperImpl<>).MakeGenericType(rt))!);

            await wrapper.Handle(request, _provider, cancellationToken);
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

        return wrapper.Handle(notification, _provider, cancellationToken);
    }
}
