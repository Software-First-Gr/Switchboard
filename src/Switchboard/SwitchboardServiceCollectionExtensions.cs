using System;
using System.Linq;
using System.Reflection;
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

    /// <summary>
    /// Registers the mediator, all handlers found in the configured assemblies,
    /// and the configured pipeline behaviors.
    /// </summary>
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

        foreach (var assembly in configuration.Assemblies)
        {
            RegisterHandlers(services, assembly);
        }

        foreach (var openBehavior in configuration.OpenBehaviors)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), openBehavior);
        }

        return services;
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
                    services.AddTransient(handlerInterface, type);
                }
            }
        }
    }
}
