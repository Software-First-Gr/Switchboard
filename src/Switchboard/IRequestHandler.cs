using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Handles an <see cref="IRequest{TResponse}"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type produced by the handler.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request and produces a response.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles a void <see cref="IRequest"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest
{
    /// <summary>Handles the request.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
