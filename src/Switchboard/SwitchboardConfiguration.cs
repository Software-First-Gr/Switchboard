using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Collects the assemblies to scan for handlers and the pipeline behaviors to register.</summary>
public sealed class SwitchboardConfiguration
{
    internal List<Assembly> Assemblies { get; } = new();

    /// <summary>
    /// Behaviors in the order they were added, open and closed generics interleaved.
    /// Registration order is preserved so the first one added always runs outermost.
    /// </summary>
    internal List<Type> Behaviors { get; } = new();

    /// <summary>Stream behaviors in the order they were added, open and closed generics interleaved, like <see cref="Behaviors"/>.</summary>
    internal List<Type> StreamBehaviors { get; } = new();

    /// <summary>Set when <see cref="UseScopePerDispatch()"/> (or an overload) was called.</summary>
    internal DispatchScopeOptions? ScopePerDispatch { get; private set; }

    /// <summary>Scans the assembly containing <typeparamref name="T"/> for handlers.</summary>
    public SwitchboardConfiguration RegisterServicesFromAssemblyContaining<T>()
        => RegisterServicesFromAssembly(typeof(T).Assembly);

    /// <summary>Scans the assembly containing <paramref name="type"/> for handlers.</summary>
    public SwitchboardConfiguration RegisterServicesFromAssemblyContaining(Type type)
        => RegisterServicesFromAssembly(type.Assembly);

    /// <summary>Scans <paramref name="assembly"/> for handlers. Adding the same assembly twice has no effect.</summary>
    public SwitchboardConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        if (!Assemblies.Contains(assembly))
        {
            Assemblies.Add(assembly);
        }

        return this;
    }

    /// <summary>
    /// Registers an open-generic pipeline behavior, e.g. <c>typeof(ValidationBehaviour&lt;,&gt;)</c>.
    /// Order matters: the first behavior added runs outermost.
    /// </summary>
    /// <remarks>
    /// The behavior must have exactly two type parameters, <c>TRequest</c> and <c>TResponse</c>: the container
    /// closes it with the two type arguments of <see cref="IPipelineBehavior{TRequest,TResponse}"/>.
    /// </remarks>
    public SwitchboardConfiguration AddOpenBehavior(Type openBehaviorType)
    {
        Behaviors.Add(RequireOpenBehavior(openBehaviorType, typeof(IPipelineBehavior<,>)));
        return this;
    }

    /// <summary>
    /// Registers a closed pipeline behavior that applies to one specific request/response pair,
    /// e.g. <c>AddBehavior&lt;AuditOrderBehavior&gt;()</c> for <c>IPipelineBehavior&lt;PlaceOrder, OrderId&gt;</c>.
    /// Order matters: the first behavior added runs outermost, counting open and closed behaviors together.
    /// </summary>
    /// <typeparam name="TBehavior">A concrete type implementing one or more closed <see cref="IPipelineBehavior{TRequest,TResponse}"/> interfaces.</typeparam>
    public SwitchboardConfiguration AddBehavior<TBehavior>()
        => AddBehavior(typeof(TBehavior));

    /// <summary>
    /// Registers a closed pipeline behavior that applies to one specific request/response pair.
    /// Order matters: the first behavior added runs outermost, counting open and closed behaviors together.
    /// </summary>
    public SwitchboardConfiguration AddBehavior(Type behaviorType)
    {
        Behaviors.Add(RequireClosedBehavior(behaviorType, typeof(IPipelineBehavior<,>), nameof(AddOpenBehavior)));
        return this;
    }

    /// <summary>
    /// Registers an open-generic stream pipeline behavior, e.g. <c>typeof(StreamLoggingBehaviour&lt;,&gt;)</c>, wrapped
    /// around every <see cref="IStreamRequestHandler{TRequest,TResponse}"/>. Order matters: the first stream behavior
    /// added runs outermost.
    /// </summary>
    /// <remarks>
    /// The behavior must have exactly two type parameters, <c>TRequest</c> and <c>TResponse</c>: the container closes it
    /// with the two type arguments of <see cref="IStreamPipelineBehavior{TRequest,TResponse}"/>.
    /// </remarks>
    public SwitchboardConfiguration AddOpenStreamBehavior(Type openBehaviorType)
    {
        StreamBehaviors.Add(RequireOpenBehavior(openBehaviorType, typeof(IStreamPipelineBehavior<,>)));
        return this;
    }

    /// <summary>
    /// Registers a closed stream pipeline behavior that applies to one specific request/item pair,
    /// e.g. <c>AddStreamBehavior&lt;RedactOrderLines&gt;()</c> for <c>IStreamPipelineBehavior&lt;GetOrderLines, OrderLine&gt;</c>.
    /// Order matters: the first stream behavior added runs outermost, counting open and closed ones together.
    /// </summary>
    /// <typeparam name="TBehavior">A concrete type implementing one or more closed <see cref="IStreamPipelineBehavior{TRequest,TResponse}"/> interfaces.</typeparam>
    public SwitchboardConfiguration AddStreamBehavior<TBehavior>()
        => AddStreamBehavior(typeof(TBehavior));

    /// <summary>
    /// Registers a closed stream pipeline behavior that applies to one specific request/item pair.
    /// Order matters: the first stream behavior added runs outermost, counting open and closed ones together.
    /// </summary>
    public SwitchboardConfiguration AddStreamBehavior(Type behaviorType)
    {
        StreamBehaviors.Add(RequireClosedBehavior(behaviorType, typeof(IStreamPipelineBehavior<,>), nameof(AddOpenStreamBehavior)));
        return this;
    }

    /// <summary>
    /// Runs every top-level <c>Send</c> and <c>Publish</c> in its own short-lived DI scope instead of
    /// the caller's, so each operation gets fresh scoped services — one <c>DbContext</c> per unit of work.
    /// Built for Blazor Server, where the caller's scope is the whole circuit and a shared
    /// <c>DbContext</c> fails with "a second operation was started on this context".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler that sends or publishes again through its injected <see cref="ISender"/> or
    /// <see cref="IPublisher"/> reuses the scope already in flight, so nested work shares one unit of work.
    /// A mediator resolved from a scope you create yourself gets its own dispatch scope instead.
    /// Work that outlives the dispatch (fire-and-forget) must not reuse it.
    /// </para>
    /// <para>
    /// When several <c>AddSwitchboard</c> calls configure this, the last callback configured wins, and a call
    /// without a callback never removes one configured elsewhere.
    /// </para>
    /// </remarks>
    public SwitchboardConfiguration UseScopePerDispatch()
    {
        ScopePerDispatch = new DispatchScopeOptions(null);
        return this;
    }

    /// <summary>
    /// Runs every top-level dispatch in its own DI scope (see <see cref="UseScopePerDispatch()"/>) and calls
    /// <paramref name="onScopeCreated"/> before the handler runs, to carry ambient state such as the
    /// current user from <see cref="DispatchScope.Parent"/> into <see cref="DispatchScope.ServiceProvider"/>.
    /// </summary>
    public SwitchboardConfiguration UseScopePerDispatch(Action<DispatchScope> onScopeCreated)
    {
        ArgumentNullException.ThrowIfNull(onScopeCreated);

        ScopePerDispatch = new DispatchScopeOptions((scope, _) =>
        {
            onScopeCreated(scope);
            return ValueTask.CompletedTask;
        });
        return this;
    }

    /// <summary>
    /// Runs every top-level dispatch in its own DI scope (see <see cref="UseScopePerDispatch()"/>) and awaits
    /// <paramref name="onScopeCreated"/> before the handler runs, to carry ambient state such as the
    /// current user from <see cref="DispatchScope.Parent"/> into <see cref="DispatchScope.ServiceProvider"/>.
    /// </summary>
    public SwitchboardConfiguration UseScopePerDispatch(Func<DispatchScope, CancellationToken, ValueTask> onScopeCreated)
    {
        ArgumentNullException.ThrowIfNull(onScopeCreated);

        ScopePerDispatch = new DispatchScopeOptions(onScopeCreated);
        return this;
    }

    internal static IEnumerable<Type> ClosedBehaviorInterfaces(Type behaviorType, Type behaviorInterface)
        => behaviorType.GetInterfaces().Where(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == behaviorInterface);

    private static Type RequireOpenBehavior(Type openBehaviorType, Type behaviorInterface)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition ||
            openBehaviorType.GetGenericArguments().Length != 2 ||
            !ClosedBehaviorInterfaces(openBehaviorType, behaviorInterface).Any())
        {
            throw new ArgumentException(
                $"{openBehaviorType} must be an open generic type with two type parameters, TRequest and TResponse, " +
                $"implementing {Name(behaviorInterface)}",
                nameof(openBehaviorType));
        }

        return openBehaviorType;
    }

    private static Type RequireClosedBehavior(Type behaviorType, Type behaviorInterface, string openAlternative)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (behaviorType.IsGenericTypeDefinition || !ClosedBehaviorInterfaces(behaviorType, behaviorInterface).Any())
        {
            throw new ArgumentException(
                $"{behaviorType} must be a concrete type implementing a closed {Name(behaviorInterface)}. " +
                $"Use {openAlternative} for open generic behaviors.",
                nameof(behaviorType));
        }

        return behaviorType;
    }

    // IPipelineBehavior`2 -> IPipelineBehavior<,>
    private static string Name(Type openInterface) => openInterface.Name[..openInterface.Name.IndexOf('`')] + "<,>";
}
