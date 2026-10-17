using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Creates a stream of the items the request's handler yields, through the stream behavior pipeline.
    /// Nothing runs until the stream is enumerated: behaviors and the handler are resolved by its first
    /// <c>MoveNextAsync</c>, and each enumeration dispatches the request again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="cancellationToken"/> and any token passed through <c>WithCancellation</c> both reach
    /// every behavior and the handler.
    /// </para>
    /// <para>
    /// Streaming was added in a minor release, so both overloads have a default implementation that throws
    /// <see cref="NotSupportedException"/>: an <see cref="ISender"/> of your own written before then, such as a
    /// decorator, still compiles and runs. Implement both, forwarding to the sender it wraps, to support streams.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => throw StreamingNotImplemented();

    /// <summary>
    /// Creates a stream for a request whose type is only known at runtime, yielding its items as objects.
    /// See <see cref="CreateStream{TResponse}"/>.
    /// </summary>
    IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
        => throw StreamingNotImplemented();

    private NotSupportedException StreamingNotImplemented()
        => new($"{GetType()} implements {nameof(ISender)} but not {nameof(CreateStream)}. " +
               $"Implement both {nameof(CreateStream)} overloads, forwarding them to the sender it wraps.");
}
