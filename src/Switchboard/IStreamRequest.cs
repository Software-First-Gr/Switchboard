namespace Switchboard;

/// <summary>A request answered with a stream of <typeparamref name="TResponse"/> items, created with <see cref="ISender.CreateStream{TResponse}"/>.</summary>
/// <typeparam name="TResponse">The type of each item the stream yields.</typeparam>
public interface IStreamRequest<out TResponse>;
