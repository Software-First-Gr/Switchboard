using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
    public SwitchboardConfiguration AddOpenBehavior(Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition ||
            !openBehaviorType.GetInterfaces().Any(
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)))
        {
            throw new ArgumentException(
                $"{openBehaviorType} must be an open generic type implementing IPipelineBehavior<,>",
                nameof(openBehaviorType));
        }

        Behaviors.Add(openBehaviorType);
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
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (behaviorType.IsGenericTypeDefinition || !ClosedBehaviorInterfaces(behaviorType).Any())
        {
            throw new ArgumentException(
                $"{behaviorType} must be a concrete type implementing a closed IPipelineBehavior<,>. " +
                "Use AddOpenBehavior for open generic behaviors.",
                nameof(behaviorType));
        }

        Behaviors.Add(behaviorType);
        return this;
    }

    internal static IEnumerable<Type> ClosedBehaviorInterfaces(Type behaviorType)
        => behaviorType.GetInterfaces().Where(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));
}
