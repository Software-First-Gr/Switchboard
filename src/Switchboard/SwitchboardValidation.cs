using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard;

/// <summary>Which checks <see cref="SwitchboardValidationExtensions.ValidateSwitchboard"/> runs. All are on by default.</summary>
public sealed class SwitchboardValidationOptions
{
    /// <summary>Every request type in a scanned assembly must have a handler.</summary>
    public bool RequireHandlerForEveryRequest { get; set; } = true;

    /// <summary>No request may have more than one handler (only the last one registered would ever run).</summary>
    public bool ForbidDuplicateHandlers { get; set; } = true;

    /// <summary>
    /// No open behavior may be silently skipped for void requests because it constrains
    /// <c>TRequest</c> to <c>IRequest&lt;TResponse&gt;</c>, which void requests do not implement.
    /// </summary>
    public bool ForbidBehaviorsThatSkipVoidRequests { get; set; } = true;
}

/// <summary>Thrown by <see cref="SwitchboardValidationExtensions.ValidateSwitchboard"/> when the registrations are inconsistent.</summary>
public sealed class SwitchboardValidationException : InvalidOperationException
{
    internal SwitchboardValidationException(IReadOnlyList<string> errors)
        : base("Switchboard registration is invalid:" + Environment.NewLine +
               string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
        => Errors = errors;

    /// <summary>Every problem found, one message each.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Startup validation of Switchboard registrations.</summary>
public static class SwitchboardValidationExtensions
{
    /// <summary>
    /// Checks the registrations without building the container or constructing a single handler, and
    /// throws a <see cref="SwitchboardValidationException"/> listing every problem: requests with no
    /// handler, requests with several, and open behaviors that never run for void requests.
    /// Call it after all registrations, just before building the provider
    /// (e.g. <c>builder.Services.ValidateSwitchboard(); var app = builder.Build();</c>).
    /// </summary>
    public static IServiceCollection ValidateSwitchboard(
        this IServiceCollection services, Action<SwitchboardValidationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SwitchboardValidationOptions();
        configure?.Invoke(options);

        var assemblies = SwitchboardServiceCollectionExtensions.GetScannedAssemblies(services)
            ?? throw new InvalidOperationException(
                $"{nameof(ValidateSwitchboard)} must be called after {nameof(SwitchboardServiceCollectionExtensions.AddSwitchboard)}.");

        // Keyed descriptors throw when their unkeyed implementation properties are read; the mediator never resolves them.
        var descriptors = services.Where(d => !d.IsKeyedService).ToList();
        var requestTypes = assemblies.SelectMany(a => a.GetTypes()).Where(IsConcreteRequest).ToList();
        var errors = new List<string>();

        if (options.RequireHandlerForEveryRequest)
        {
            errors.AddRange(FindMissingHandlers(requestTypes, descriptors));
        }

        if (options.ForbidDuplicateHandlers)
        {
            errors.AddRange(FindDuplicateHandlers(descriptors));
        }

        if (options.ForbidBehaviorsThatSkipVoidRequests)
        {
            errors.AddRange(FindBehaviorsThatSkipVoidRequests(requestTypes, descriptors));
        }

        if (errors.Count > 0)
        {
            throw new SwitchboardValidationException(errors);
        }

        return services;
    }

    private static IEnumerable<string> FindMissingHandlers(List<Type> requestTypes, List<ServiceDescriptor> descriptors)
    {
        var registered = descriptors.Select(d => d.ServiceType).ToHashSet();

        foreach (var requestType in requestTypes)
        {
            foreach (var handlerType in HandlerServiceTypes(requestType))
            {
                if (!registered.Contains(handlerType) && !registered.Contains(handlerType.GetGenericTypeDefinition()))
                {
                    yield return $"{Name(requestType)} has no handler. Add a class implementing {Name(handlerType)}.";
                }
            }
        }
    }

    private static IEnumerable<string> FindDuplicateHandlers(List<ServiceDescriptor> descriptors)
    {
        var handlerRegistrations = descriptors
            .Where(d => d.ServiceType.IsConstructedGenericType &&
                        d.ServiceType.GetGenericTypeDefinition() is var definition &&
                        (definition == typeof(IRequestHandler<,>) || definition == typeof(IRequestHandler<>)))
            .GroupBy(d => d.ServiceType);

        foreach (var group in handlerRegistrations)
        {
            // Registering the very same class twice is harmless (either one runs); two different ones are not.
            var implementations = group.Select(Describe).Distinct().ToList();
            if (implementations.Count > 1)
            {
                yield return $"{Name(group.Key.GetGenericArguments()[0])} has {implementations.Count} handlers " +
                             $"({string.Join(", ", implementations)}); only the last one registered would ever run.";
            }
        }
    }

    private static IEnumerable<string> FindBehaviorsThatSkipVoidRequests(List<Type> requestTypes, List<ServiceDescriptor> descriptors)
    {
        var voidRequests = requestTypes.Where(t => typeof(IRequest).IsAssignableFrom(t)).ToList();
        if (voidRequests.Count == 0)
        {
            yield break;
        }

        var openBehaviors = descriptors
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) && d.ImplementationType is { IsGenericTypeDefinition: true })
            .Select(d => d.ImplementationType!)
            .Distinct();

        foreach (var behavior in openBehaviors)
        {
            // Skipped = the container refuses to close the behavior over (request, Unit), yet it would accept it
            // if not for an IRequest<TResponse> constraint. Constraints that exclude a request on purpose
            // (e.g. where TRequest : ICacheableQuery) fail both checks and are left alone.
            var skipped = voidRequests
                .Where(r => !CanClose(behavior, r) && SatisfiesConstraints(behavior, new[] { r, typeof(Unit) }, ignoreTypedRequestConstraint: true))
                .Select(Name)
                .ToList();

            if (skipped.Count > 0)
            {
                var listed = string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? $" and {skipped.Count - 5} more" : "");
                yield return $"{Name(behavior)} never runs for {skipped.Count} void request(s) ({listed}) because it constrains " +
                             "TRequest to IRequest<TResponse>, which void requests do not implement. " +
                             "Constrain it to IBaseRequest to cover every request.";
            }
        }
    }

    private static bool IsConcreteRequest(Type type)
        => !type.IsAbstract && !type.IsInterface && !type.IsGenericTypeDefinition && typeof(IBaseRequest).IsAssignableFrom(type);

    private static IEnumerable<Type> HandlerServiceTypes(Type requestType)
    {
        foreach (var requestInterface in requestType.GetInterfaces())
        {
            if (requestInterface.IsGenericType && requestInterface.GetGenericTypeDefinition() == typeof(IRequest<>))
            {
                yield return typeof(IRequestHandler<,>).MakeGenericType(requestType, requestInterface.GetGenericArguments()[0]);
            }
        }

        if (typeof(IRequest).IsAssignableFrom(requestType))
        {
            yield return typeof(IRequestHandler<>).MakeGenericType(requestType);
        }
    }

    /// <summary>Exactly what the container does when it decides whether an open behavior applies.</summary>
    private static bool CanClose(Type openBehavior, Type requestType)
    {
        try
        {
            openBehavior.MakeGenericType(requestType, typeof(Unit));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SatisfiesConstraints(Type openType, Type[] arguments, bool ignoreTypedRequestConstraint)
    {
        var parameters = openType.GetGenericArguments();

        for (var i = 0; i < parameters.Length; i++)
        {
            var argument = arguments[i];
            var attributes = parameters[i].GenericParameterAttributes;

            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && argument.IsValueType)
            {
                return false;
            }

            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) &&
                (!argument.IsValueType || Nullable.GetUnderlyingType(argument) is not null))
            {
                return false;
            }

            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) &&
                !argument.IsValueType && argument.GetConstructor(Type.EmptyTypes) is null)
            {
                return false;
            }

            foreach (var constraint in parameters[i].GetGenericParameterConstraints())
            {
                if (ignoreTypedRequestConstraint &&
                    constraint.IsGenericType && constraint.GetGenericTypeDefinition() == typeof(IRequest<>))
                {
                    continue;
                }

                var closed = Substitute(constraint, arguments);
                if (closed is null || !closed.IsAssignableFrom(argument))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Type? Substitute(Type type, Type[] arguments)
    {
        if (type.IsGenericParameter)
        {
            return arguments[type.GenericParameterPosition];
        }

        if (!type.ContainsGenericParameters)
        {
            return type;
        }

        var closedArguments = type.GetGenericArguments().Select(a => Substitute(a, arguments)).ToArray();
        if (closedArguments.Any(a => a is null))
        {
            return null;
        }

        try
        {
            return type.GetGenericTypeDefinition().MakeGenericType(closedArguments!);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Describe(ServiceDescriptor descriptor)
        => descriptor.ImplementationType is { } type ? Name(type)
            : descriptor.ImplementationInstance is { } instance ? Name(instance.GetType())
            : "a factory registration";

    private static string Name(Type type) => SwitchboardTelemetry.DisplayName(type);
}
