using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed class StreamRegistrationTests
{
    private static IServiceCollection Scanned(Action<SwitchboardConfiguration>? configure = null)
        => new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddScoped<ScopeMarker>()
            .AddSwitchboard(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<StreamRegistrationTests>();
                configure?.Invoke(cfg);
            });

    [Fact]
    public void Scanning_registers_stream_handlers()
    {
        using var provider = Scanned().BuildServiceProvider();

        Assert.IsType<CountToHandler>(Assert.Single(provider.GetServices<IStreamRequestHandler<CountTo, int>>()));
    }

    [Fact]
    public void AddOpenStreamBehavior_and_AddStreamBehavior_register_in_order()
    {
        using var provider = Scanned(cfg => cfg
                .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
                .AddStreamBehavior<EvenOnlyBehavior>()
                .AddOpenStreamBehavior(typeof(SecondStreamBehavior<,>)))
            .BuildServiceProvider();

        var behaviors = provider.GetServices<IStreamPipelineBehavior<CountTo, int>>().ToList();

        Assert.Equal(3, behaviors.Count);
        Assert.IsType<FirstStreamBehavior<CountTo, int>>(behaviors[0]);
        Assert.IsType<EvenOnlyBehavior>(behaviors[1]);
        Assert.IsType<SecondStreamBehavior<CountTo, int>>(behaviors[2]);
    }

    [Fact]
    public void Stream_behaviors_are_not_registered_as_request_behaviors_or_the_other_way_round()
    {
        using var provider = Scanned(cfg => cfg
                .AddOpenBehavior(typeof(FirstBehavior<,>))
                .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>)))
            .BuildServiceProvider();

        Assert.IsType<FirstBehavior<Ping, string>>(Assert.Single(provider.GetServices<IPipelineBehavior<Ping, string>>()));
        Assert.IsType<FirstStreamBehavior<CountTo, int>>(Assert.Single(provider.GetServices<IStreamPipelineBehavior<CountTo, int>>()));
    }

    [Fact]
    public void Calling_AddSwitchboard_twice_registers_stream_handlers_and_behaviors_once()
    {
        using var provider = Scanned(cfg => cfg
                .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
                .AddStreamBehavior<EvenOnlyBehavior>())
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<StreamRegistrationTests>()
                .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
                .AddStreamBehavior<EvenOnlyBehavior>())
            .BuildServiceProvider();

        Assert.Single(provider.GetServices<IStreamRequestHandler<CountTo, int>>());
        Assert.Equal(2, provider.GetServices<IStreamPipelineBehavior<CountTo, int>>().Count());
    }

    [Fact]
    public void AddOpenStreamBehavior_rejects_types_that_are_not_open_generic_stream_behaviors()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSwitchboard(cfg => cfg.AddOpenStreamBehavior(null!)));
        Assert.Throws<ArgumentException>(() => services.AddSwitchboard(cfg => cfg.AddOpenStreamBehavior(typeof(EvenOnlyBehavior))));
        var requestBehavior = Assert.Throws<ArgumentException>(
            () => services.AddSwitchboard(cfg => cfg.AddOpenStreamBehavior(typeof(FirstBehavior<,>))));

        Assert.Contains("IStreamPipelineBehavior<,>", requestBehavior.Message);
    }

    [Fact]
    public void AddStreamBehavior_rejects_open_generics_and_non_stream_behaviors()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSwitchboard(cfg => cfg.AddStreamBehavior(null!)));
        var open = Assert.Throws<ArgumentException>(
            () => services.AddSwitchboard(cfg => cfg.AddStreamBehavior(typeof(FirstStreamBehavior<,>))));
        Assert.Throws<ArgumentException>(() => services.AddSwitchboard(cfg => cfg.AddStreamBehavior<ClosedTrackedBehavior>()));

        Assert.Contains("AddOpenStreamBehavior", open.Message);
    }

    [Fact]
    public void Validation_reports_a_stream_request_without_a_handler()
    {
        var exception = Assert.Throws<SwitchboardValidationException>(() => Scanned().ValidateSwitchboard());

        Assert.Contains(
            "OrphanStream has no handler. Add a class implementing IStreamRequestHandler<OrphanStream, Int32>.",
            exception.Errors);
        Assert.DoesNotContain(exception.Errors, e => e.StartsWith("CountTo "));
    }

    [Fact]
    public void Validation_reports_a_stream_request_with_two_different_handlers()
    {
        var services = Scanned().AddTransient<IStreamRequestHandler<CountTo, int>>(sp => new CountToHandler(new Recorder()));

        var exception = Assert.Throws<SwitchboardValidationException>(
            () => services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false));

        Assert.Equal(
            "CountTo has 2 handlers (CountToHandler, a factory registration); only the last one registered would ever run.",
            Assert.Single(exception.Errors));
    }

    [Fact]
    public void Validation_passes_with_stream_handlers_and_behaviors_registered()
    {
        var services = Scanned(cfg => cfg
            .AddOpenStreamBehavior(typeof(FirstStreamBehavior<,>))
            .AddStreamBehavior<EvenOnlyBehavior>());

        services.ValidateSwitchboard(o => o.RequireHandlerForEveryRequest = false);
    }
}
