using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Switchboard;

/// <summary>Collects the assemblies to scan for handlers and the pipeline behaviors to register.</summary>
public sealed class SwitchboardConfiguration
{
    internal List<Assembly> Assemblies { get; } = new();
    internal List<Type> OpenBehaviors { get; } = new();

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

        OpenBehaviors.Add(openBehaviorType);
        return this;
    }
}
