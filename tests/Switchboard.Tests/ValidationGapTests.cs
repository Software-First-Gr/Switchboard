using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

// --- Request shapes validation must not mistake for requests that need a handler ---

/// <summary>A base request: only the concrete requests derived from it are ever sent.</summary>
public abstract record PagedQuery : IRequest<int>;

public sealed record CountWidgets : PagedQuery;

public sealed class CountWidgetsHandler : IRequestHandler<CountWidgets, int>
{
    public Task<int> Handle(CountWidgets request, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>A marker interface for a family of commands; never sent itself.</summary>
public interface IVoidCommand : IRequest;

/// <summary>An open generic request: only its closed forms can ever be sent.</summary>
public sealed record GenericLookup<T>(int Id) : IRequest<T>;

/// <summary>A void request without a parameterless constructor.</summary>
public sealed record VoidWithArgument(int Value) : IRequest;

public sealed class VoidWithArgumentHandler : IRequestHandler<VoidWithArgument>
{
    public Task Handle(VoidWithArgument request, CancellationToken cancellationToken) => Task.CompletedTask;
}

// --- Behaviors that constrain TRequest to IRequest<TResponse> plus something else ---

/// <summary>The trap, narrowed to <see cref="IAudited"/> requests: it skips exactly the audited void requests.</summary>
public sealed class AuditedTypedOnlyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited, IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>Only for reference-type responses (a cache, say), so it never applies to <see cref="Unit"/> anyway.</summary>
public sealed class ReferenceResponseBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : class
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>Only for value-type responses, which <see cref="Unit"/> is: void requests are skipped by the trap alone.</summary>
public sealed class ValueResponseBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : struct
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>Only for value-type requests; every void request in this assembly is a class.</summary>
public sealed class ValueRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : struct, IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>Only for requests with a parameterless constructor.</summary>
public sealed class NewableRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, new()
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

/// <summary>A constraint that refers to the behavior's own type parameter, as a de-duplicating behavior might use.</summary>
public sealed class EquatableRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IEquatable<TRequest>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

// --- An open generic service that is not a pipeline behavior ---

public interface IRequestAuditor<TRequest, TResponse>;

public sealed class TypedRequestAuditor<TRequest, TResponse> : IRequestAuditor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
}

public sealed class ValidationGapTests
{
    private static IServiceCollection Scanned(Action<SwitchboardConfiguration>? configure = null)
        => new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<ValidationGapTests>();
                configure?.Invoke(cfg);
            });

    private static SwitchboardValidationException Invalid(IServiceCollection services, Action<SwitchboardValidationOptions>? configure = null)
        => Assert.Throws<SwitchboardValidationException>(() => services.ValidateSwitchboard(configure));

    private static string[] SkipErrors(Type openBehavior)
    {
        var exception = Invalid(Scanned(cfg => cfg.AddOpenBehavior(openBehavior)), o => o.RequireHandlerForEveryRequest = false);
        return exception.Errors.ToArray();
    }

    /// <summary>Every concrete void request this assembly declares, the set validation scans.</summary>
    private static Type[] VoidRequests()
        => typeof(ValidationGapTests).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && typeof(IRequest).IsAssignableFrom(t))
            .ToArray();

    private static (int Count, string[] Listed, int? More) ParseSkipped(string error)
    {
        var match = Regex.Match(
            error, @"never runs for (?<count>\d+) void request\(s\) \((?<listed>[^()]*?)(?: and (?<more>\d+) more)?\) because it constrains ");
        Assert.True(match.Success, error);

        return (
            int.Parse(match.Groups["count"].Value),
            match.Groups["listed"].Value.Split(", "),
            match.Groups["more"].Success ? int.Parse(match.Groups["more"].Value) : null);
    }

    [Fact]
    public void Reports_a_void_request_without_a_handler()
    {
        var exception = Invalid(Scanned());

        Assert.Contains("VoidOrphan has no handler. Add a class implementing IRequestHandler<VoidOrphan>.", exception.Errors);
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("FireAndForget "));
    }

    [Fact]
    public void Abstract_interface_and_open_generic_request_types_are_not_reported_as_missing_a_handler()
    {
        var exception = Invalid(Scanned());

        Assert.Contains(exception.Errors, e => e.StartsWith("Orphan "));
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("PagedQuery "));
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("CountWidgets "));
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("IVoidCommand "));
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("GenericLookup<"));
    }

    [Fact]
    public void A_typed_only_behavior_names_five_void_requests_and_counts_the_rest()
    {
        // The count covers every void request, VoidWithArgument (no parameterless constructor) included.
        var voidRequests = VoidRequests().Select(SwitchboardTelemetry.DisplayName).ToArray();
        Assert.True(voidRequests.Length > 5, "this test needs more than five void requests in the assembly");

        var (count, listed, more) = ParseSkipped(Assert.Single(SkipErrors(typeof(TypedOnlyBehavior<,>))));

        Assert.Equal(voidRequests.Length, count);
        Assert.Equal(5, listed.Length);
        Assert.Equal(5, listed.Distinct().Count());
        Assert.All(listed, name => Assert.Contains(name, voidRequests));
        Assert.Equal(voidRequests.Length - 5, more);
    }

    [Fact]
    public void A_behavior_that_also_requires_a_marker_is_reported_for_exactly_the_void_requests_that_carry_it()
    {
        var error = Assert.Single(SkipErrors(typeof(AuditedTypedOnlyBehavior<,>)));

        Assert.Equal(
            "AuditedTypedOnlyBehavior<TRequest, TResponse> never runs for 1 void request(s) (AuditedVoidCommand) because it constrains " +
            "TRequest to IRequest<TResponse>, which void requests do not implement. Constrain it to IBaseRequest to cover every request.",
            error);
    }

    [Fact]
    public void A_behavior_that_requires_a_reference_type_response_is_not_reported()
    {
        // It could never run for a Unit response, IRequest<TResponse> or not: excluding void requests is on purpose.
        Scanned(cfg => cfg.AddOpenBehavior(typeof(ReferenceResponseBehavior<,>)))
            .ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void A_behavior_that_requires_a_value_type_response_is_still_reported()
    {
        // Unit is a value type, so only the IRequest<TResponse> constraint keeps it away from void requests.
        var (count, _, _) = ParseSkipped(Assert.Single(SkipErrors(typeof(ValueResponseBehavior<,>))));

        Assert.Equal(VoidRequests().Length, count);
    }

    [Fact]
    public void A_behavior_limited_to_value_type_requests_is_not_reported_for_void_requests_that_are_classes()
    {
        Assert.All(VoidRequests(), t => Assert.False(t.IsValueType));

        Scanned(cfg => cfg.AddOpenBehavior(typeof(ValueRequestBehavior<,>)))
            .ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void A_behavior_that_requires_a_parameterless_constructor_counts_only_the_void_requests_that_have_one()
    {
        var newable = VoidRequests().Count(t => t.GetConstructor(Type.EmptyTypes) is not null);
        Assert.True(newable > 0 && newable < VoidRequests().Length, "this test needs void requests with and without a parameterless constructor");

        var (count, listed, _) = ParseSkipped(Assert.Single(SkipErrors(typeof(NewableRequestBehavior<,>))));

        Assert.Equal(newable, count);
        Assert.DoesNotContain(nameof(VoidWithArgument), listed);
    }

    [Fact]
    public void A_constraint_on_the_behaviors_own_type_parameter_is_checked_against_each_request()
    {
        var equatable = VoidRequests().Count(t => typeof(IEquatable<>).MakeGenericType(t).IsAssignableFrom(t));
        Assert.True(equatable > 0, "records implement IEquatable<T> of themselves");

        var (count, _, _) = ParseSkipped(Assert.Single(SkipErrors(typeof(EquatableRequestBehavior<,>))));

        Assert.Equal(equatable, count);
    }

    [Fact]
    public void An_open_generic_service_that_is_not_a_pipeline_behavior_is_never_reported()
    {
        var services = Scanned().AddTransient(typeof(IRequestAuditor<,>), typeof(TypedRequestAuditor<,>));

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void ValidateSwitchboard_names_the_null_services_argument()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => SwitchboardValidationExtensions.ValidateSwitchboard(null!));

        Assert.Equal("services", exception.ParamName);
    }
}
