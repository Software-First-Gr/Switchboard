using System;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>
/// The scope created for one top-level dispatch when
/// <see cref="SwitchboardConfiguration.UseScopePerDispatch()"/> is on. Passed to the
/// <c>onScopeCreated</c> callback before the handler runs, so ambient state (the current user,
/// a tenant id, a correlation id) can be carried from the caller's scope into the new one.
/// </summary>
public sealed class DispatchScope
{
    internal DispatchScope(IServiceProvider parent, IServiceProvider serviceProvider, object message)
    {
        Parent = parent;
        ServiceProvider = serviceProvider;
        Message = message;
    }

    /// <summary>The provider the mediator was resolved from: the caller's scope (an HTTP request, a Blazor circuit).</summary>
    public IServiceProvider Parent { get; }

    /// <summary>The fresh scope the handler, its behaviors and their dependencies are resolved from.</summary>
    public IServiceProvider ServiceProvider { get; }

    /// <summary>The request or notification being dispatched.</summary>
    public object Message { get; }
}

internal sealed class DispatchScopeOptions
{
    public DispatchScopeOptions(Func<DispatchScope, CancellationToken, ValueTask>? onScopeCreated)
        => OnScopeCreated = onScopeCreated;

    public Func<DispatchScope, CancellationToken, ValueTask>? OnScopeCreated { get; }
}
