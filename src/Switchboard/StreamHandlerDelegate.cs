using System.Collections.Generic;

namespace Switchboard;

/// <summary>
/// Represents the continuation of the stream pipeline: the next behavior, or the handler itself
/// when invoked from the innermost behavior.
/// </summary>
/// <remarks>
/// Unlike <see cref="RequestHandlerDelegate{TResponse}"/> it takes no token, as in MediatR: everything inside
/// a behavior already receives the stream's token. To hand the rest of the pipeline a different one, enumerate
/// <c>next().WithCancellation(token)</c>; an iterator that marks its token <c>[EnumeratorCancellation]</c> receives it.
/// </remarks>
/// <typeparam name="TResponse">The type of each item the stream yields.</typeparam>
/// <returns>The items produced by the rest of the pipeline.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();
