using System.Collections.Generic;
using System.Threading;

namespace Switchboard;

/// <summary>Handles an <see cref="IStreamRequest{TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The type of each item the handler yields.</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Handles the request and yields its items.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel the stream. It already includes any token the consumer passed through
    /// <c>WithCancellation</c>.
    /// </param>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
