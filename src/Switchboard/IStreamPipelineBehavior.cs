using System.Collections.Generic;
using System.Threading;

namespace Switchboard;

/// <summary>Cross-cutting behavior wrapped around a stream request handler.</summary>
/// <typeparam name="TRequest">The stream request type this behavior applies to.</typeparam>
/// <typeparam name="TResponse">The type of each item the stream yields.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
{
    /// <summary>
    /// Executes the behavior. Enumerate <paramref name="next"/> to continue the pipeline, yielding its items
    /// as they are, transformed or filtered, or yield items of your own without calling it to short-circuit.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The rest of the pipeline: the next behavior, or the handler when this is the innermost behavior.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel the stream. It already includes any token the consumer passed through
    /// <c>WithCancellation</c>.
    /// </param>
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
