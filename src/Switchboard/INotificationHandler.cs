using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Handles an <see cref="INotification"/>. Multiple handlers may exist per notification.</summary>
/// <typeparam name="TNotification">The notification type.</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>Handles the notification.</summary>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
