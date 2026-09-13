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
        typeof(INotificationHandler<>)
    };

    // Every assembly scanned into a collection, across all AddSwitchboard calls, for ValidateSwitchboard.
    private static readonly ConditionalWeakTable<IServiceCollection, List<Assembly>> ScannedAssemblies = new();

    /// <summary>
    /// Registers the mediator, all handlers found in the configured assemblies,
    /// and the configured pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once, e.g. from several modules scanning a shared assembly: a handler or
    /// behavior already registered is not registered again, so it never runs twice.
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
            services.TryAddSingleton(scopePerDispatch);
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

        // Registered in the order they were configured, so DI hands them back in that same
        // order and the first one added ends up outermost — open and closed alike.
        foreach (var behavior in configuration.Behaviors)
        {
            if (behavior.IsGenericTypeDefinition)
            {
                services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), behavior));
                continue;
            }

            foreach (var closedInterface in SwitchboardConfiguration.ClosedBehaviorInterfaces(behavior))
            {
                services.TryAddEnumerable(ServiceDescriptor.Transient(closedInterface, behavior));
            }
        }

        return services;
    }

    internal static IReadOnlyList<Assembly>? GetScannedAssemblies(IServiceCollection services)
        => ScannedAssemblies.TryGetValue(services, out var assemblies) ? assemblies : null;

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
