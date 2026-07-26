using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Sends a request to its single handler through the behavior pipeline.</summary>
public interface ISender
{
    /// <summary>Sends a request expecting a <typeparamref name="TResponse"/>.</summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a void request.</summary>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest;

    /// <summary>
    /// Sends a request whose type is only known at runtime.
    /// Returns the response, or <see langword="null"/> for void requests.
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}
