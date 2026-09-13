using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>
/// Represents the continuation of the request pipeline: the next behavior, or the handler itself
/// when invoked from the innermost behavior.
/// </summary>
/// <typeparam name="TResponse">The response type produced by the pipeline.</typeparam>
/// <param name="cancellationToken">
/// Optional. Pass a token to hand the rest of the pipeline a different one, e.g. a linked token that adds
/// a timeout. Pass nothing (or <see langword="default"/>) to keep the token this behavior received, so
/// calling <c>next()</c> with no arguments never loses cancellation.
/// </param>
/// <returns>The response produced by the rest of the pipeline.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);
