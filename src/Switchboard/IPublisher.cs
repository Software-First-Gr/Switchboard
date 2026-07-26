using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Publishes a notification to all registered handlers.</summary>
public interface IPublisher
{
    /// <summary>Publishes a notification whose type is only known at runtime.</summary>
    Task Publish(object notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a notification to every registered <see cref="INotificationHandler{TNotification}"/>,
    /// sequentially, in registration order.
    /// </summary>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
