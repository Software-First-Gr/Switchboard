using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Cross-cutting behavior wrapped around a request handler.</summary>
/// <typeparam name="TRequest">The request type this behavior applies to.</typeparam>
/// <typeparam name="TResponse">The response type produced by the pipeline.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
{
    /// <summary>
    /// Executes the behavior. Call <paramref name="next"/> to continue the pipeline,
    /// or return without calling it to short-circuit.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The rest of the pipeline: the next behavior, or the handler when this is the innermost behavior.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
