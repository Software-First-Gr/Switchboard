using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Switchboard;

/// <summary>Dependency-injection registration for Switchboard.</summary>
public static class SwitchboardServiceCollectionExtensions
{
    private static readonly Type[] HandlerInterfaces =
    {
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(INotificationHandler<>),
        typeof(IStreamRequestHandler<,>)
    };

    // Every assembly scanned into a collection, across all AddSwitchboard calls, for ValidateSwitchboard.
    private static readonly ConditionalWeakTable<IServiceCollection, List<Assembly>> ScannedAssemblies = new();

    /// <summary>
    /// Registers the mediator, all handlers found in the configured assemblies (request, notification and
    /// stream request handlers), and the configured pipeline and stream pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once, e.g. from several modules scanning a shared assembly: a handler or
    /// behavior already registered is not registered again, so it never runs twice. A later call may switch
    /// on scope-per-dispatch or add its callback; the last callback configured wins, and a bare
    /// <see cref="SwitchboardConfiguration.UseScopePerDispatch()"/> never removes one configured earlier.
    /// </remarks>
    public static IServiceCollection AddSwitchboard(this IServiceCollection services, Action<SwitchboardConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new SwitchboardConfiguration();
        configure(configuration);

        services.TryAddTransient<Mediator>();
        services.TryAddTransient<IMediator>(sp => sp.GetRequiredService<Mediator>());
        services.TryAddTransient<ISender>(sp => sp.GetRequiredService<Mediator>());
        services.TryAddTransient<IPublisher>(sp => sp.GetRequiredService<Mediator>());

        if (configuration.ScopePerDispatch is { } scopePerDispatch)
        {
            // A later AddSwitchboard call (the host layer adding the current-user callback, say) may add or
            // replace the callback, but a bare UseScopePerDispatch() must never remove one configured elsewhere.
            var alreadyConfigured = services.Any(d => d.ServiceType == typeof(DispatchScopeOptions));

            if (!alreadyConfigured)
            {
                services.AddSingleton(scopePerDispatch);
            }
            else if (scopePerDispatch.OnScopeCreated is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(scopePerDispatch));
            }
        }

        var scanned = ScannedAssemblies.GetOrCreateValue(services);
        foreach (var assembly in configuration.Assemblies)
        {
            if (!scanned.Contains(assembly))
            {
                scanned.Add(assembly);
            }

            RegisterHandlers(services, assembly);
        }

        RegisterBehaviors(services, configuration.Behaviors, typeof(IPipelineBehavior<,>));
        RegisterBehaviors(services, configuration.StreamBehaviors, typeof(IStreamPipelineBehavior<,>));

        return services;
    }

    internal static IReadOnlyList<Assembly>? GetScannedAssemblies(IServiceCollection services)
        => ScannedAssemblies.TryGetValue(services, out var assemblies) ? assemblies : null;

    // Registered in the order they were configured, so DI hands them back in that same
    // order and the first one added ends up outermost — open and closed alike.
    private static void RegisterBehaviors(IServiceCollection services, List<Type> behaviors, Type behaviorInterface)
    {
        foreach (var behavior in behaviors)
        {
            if (behavior.IsGenericTypeDefinition)
            {
                services.TryAddEnumerable(ServiceDescriptor.Transient(behaviorInterface, behavior));
                continue;
            }

            foreach (var closedInterface in SwitchboardConfiguration.ClosedBehaviorInterfaces(behavior, behaviorInterface))
            {
                services.TryAddEnumerable(ServiceDescriptor.Transient(closedInterface, behavior));
            }
        }
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                continue;
            }

            foreach (var handlerInterface in type.GetInterfaces())
            {
                if (handlerInterface.IsGenericType &&
                    HandlerInterfaces.Contains(handlerInterface.GetGenericTypeDefinition()))
                {
                    // TryAddEnumerable skips an identical service/implementation pair, so scanning the
                    // same assembly from two AddSwitchboard calls cannot make a handler run twice.
                    services.TryAddEnumerable(ServiceDescriptor.Transient(handlerInterface, type));
                }
            }
        }
    }
}
