using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>
/// Represents the continuation of the request pipeline: the next behavior, or the handler itself
/// when invoked from the innermost behavior.
/// </summary>
/// <typeparam name="TResponse">The response type produced by the pipeline.</typeparam>
/// <param name="cancellationToken">
/// Optional token. Regardless of what is passed here, the pipeline propagates the token originally
/// given to <c>Send</c>, so calling <c>next()</c> with no arguments never loses cancellation.
/// </param>
/// <returns>The response produced by the rest of the pipeline.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);
