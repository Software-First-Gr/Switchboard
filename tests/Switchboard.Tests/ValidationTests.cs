using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

/// <summary>The trap: copied from many templates, it silently excludes every void request.</summary>
public sealed class TypedOnlyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly Recorder _recorder;

    public TypedOnlyBehavior(Recorder recorder) => _recorder = recorder;

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("typed-only");
        return next(cancellationToken);
    }
}

/// <summary>The fix: constrained to the marker every request implements.</summary>
public sealed class AnyRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IBaseRequest
{
    private readonly Recorder _recorder;

    public AnyRequestBehavior(Recorder recorder) => _recorder = recorder;

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _recorder.Add("any-request");
        return next(cancellationToken);
    }
}

public sealed class ValidationTests
{
    private static IServiceCollection Scanned(Action<SwitchboardConfiguration>? configure = null)
        => new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<ValidationTests>();
                configure?.Invoke(cfg);
            });

    private static SwitchboardValidationException Invalid(IServiceCollection services, Action<SwitchboardValidationOptions>? configure = null)
        => Assert.Throws<SwitchboardValidationException>(() => services.ValidateSwitchboard(configure));

    [Fact]
    public void Reports_a_request_without_a_handler()
    {
        var exception = Invalid(Scanned());

        var error = Assert.Single(exception.Errors, e => e.StartsWith("Orphan "));
        Assert.Contains("IRequestHandler<Orphan, Int32>", error);
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("Ping "));
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void Passes_when_every_check_is_satisfied()
    {
        var services = Scanned(cfg => cfg.AddOpenBehavior(typeof(AnyRequestBehavior<,>)));

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void Reports_a_request_with_two_different_handlers()
    {
        var services = Scanned().AddTransient<IRequestHandler<Ping, string>>(_ => new PingHandler());

        var exception = Invalid(services, o => o.RequireHandlerForEveryRequest = false);

        var error = Assert.Single(exception.Errors);
        Assert.Equal("Ping has 2 handlers (PingHandler, a factory registration); only the last one registered would ever run.", error);
    }

    [Fact]
    public void Registering_the_same_handler_class_twice_is_not_a_duplicate()
    {
        var services = Scanned().AddTransient<IRequestHandler<Ping, string>, PingHandler>();

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void Reports_a_behavior_that_silently_skips_void_requests()
    {
        var services = Scanned(cfg => cfg.AddOpenBehavior(typeof(TypedOnlyBehavior<,>)));

        var exception = Invalid(services, o => o.RequireHandlerForEveryRequest = false);

        var error = Assert.Single(exception.Errors);
        Assert.StartsWith("TypedOnlyBehavior<TRequest, TResponse> never runs for", error);
        Assert.Contains("VoidTracked", error);
        Assert.Contains("IBaseRequest", error);
    }

    [Fact]
    public void Detects_behaviors_registered_directly_on_the_container_too()
    {
        var services = Scanned().AddTransient(typeof(IPipelineBehavior<,>), typeof(TypedOnlyBehavior<,>));

        var exception = Invalid(services, o => o.RequireHandlerForEveryRequest = false);

        Assert.Contains(exception.Errors, e => e.StartsWith("TypedOnlyBehavior"));
    }

    [Fact]
    public void Does_not_crash_on_a_behavior_registered_directly_that_the_container_cannot_close()
    {
        // Wrong arity: the container reports it itself when the provider is built. Validation must not fail before that with an index error.
        var services = Scanned().AddTransient(typeof(IPipelineBehavior<,>), typeof(ThreeParameterBehavior<,,>));

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void Does_not_report_behaviors_that_exclude_requests_on_purpose()
    {
        var services = Scanned(cfg => cfg
            .AddOpenBehavior(typeof(AnyRequestBehavior<,>))
            .AddOpenBehavior(typeof(AuditedOnlyBehavior<,>)));

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void Each_check_can_be_switched_off()
    {
        var services = Scanned(cfg => cfg.AddOpenBehavior(typeof(TypedOnlyBehavior<,>)))
            .AddTransient<IRequestHandler<Ping, string>>(_ => new PingHandler());

        services.ValidateSwitchboard(o =>
        {
            o.RequireHandlerForEveryRequest = false;
            o.ForbidDuplicateHandlers = false;
            o.ForbidBehaviorsThatSkipVoidRequests = false;
        });
    }

    [Fact]
    public void Keyed_registrations_do_not_break_validation()
    {
        var services = Scanned().AddKeyedTransient<Recorder>("keyed");

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }

    [Fact]
    public void Requires_AddSwitchboard_first()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().ValidateSwitchboard());
    }

    [Fact]
    public async Task The_trap_is_real_at_runtime_and_IBaseRequest_fixes_it()
    {
        await using var provider = Scanned(cfg => cfg
                .AddOpenBehavior(typeof(TypedOnlyBehavior<,>))
                .AddOpenBehavior(typeof(AnyRequestBehavior<,>)))
            .BuildServiceProvider();
        var recorder = provider.GetRequiredService<Recorder>();

        await provider.GetRequiredService<ISender>().Send(new FireAndForget());

        Assert.Equal("any-request|fire-and-forget", recorder.Joined);
    }
}
