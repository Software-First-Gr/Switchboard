namespace Switchboard;

/// <summary>A request that returns a <typeparamref name="TResponse"/> when handled.</summary>
/// <typeparam name="TResponse">The type of value the request produces.</typeparam>
public interface IRequest<out TResponse> : IBaseRequest;

/// <summary>A request that returns no value (handled as <see cref="Unit"/> internally).</summary>
public interface IRequest : IBaseRequest;
