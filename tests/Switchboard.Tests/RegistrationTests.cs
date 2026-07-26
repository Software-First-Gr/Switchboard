using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.Tests;

public sealed class RegistrationTests
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg.RegisterServicesFromAssemblyContaining<RegistrationTests>())
            .BuildServiceProvider();

    [Fact]
    public void AddSwitchboard_registers_mediator_sender_and_publisher()
    {
        using var provider = BuildProvider();

        Assert.IsType<Mediator>(provider.GetRequiredService<IMediator>());
        Assert.IsType<Mediator>(provider.GetRequiredService<ISender>());
        Assert.IsType<Mediator>(provider.GetRequiredService<IPublisher>());
    }

    [Fact]
    public void Scanning_registers_concrete_handlers_and_skips_abstract_and_open_generic_types()
    {
        using var provider = BuildProvider();

        var pingHandlers = provider.GetServices<IRequestHandler<Ping, string>>().ToList();

        var single = Assert.Single(pingHandlers);
        Assert.IsType<PingHandler>(single);
    }

    [Fact]
    public void Scanning_registers_void_and_notification_handlers()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IRequestHandler<FireAndForget>>());
        Assert.NotNull(provider.GetService<INotificationHandler<OrderedNote>>());
    }

    [Fact]
    public void Registering_the_same_assembly_twice_registers_handlers_once()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<RegistrationTests>()
                .RegisterServicesFromAssembly(typeof(RegistrationTests).Assembly))
            .BuildServiceProvider();

        Assert.Single(provider.GetServices<IRequestHandler<Ping, string>>());
    }

    [Fact]
    public void Calling_AddSwitchboard_twice_keeps_a_single_mediator_registration()
    {
        using var provider = new ServiceCollection()
            .AddSwitchboard(cfg => { })
            .AddSwitchboard(cfg => { })
            .BuildServiceProvider();

        Assert.Single(provider.GetServices<IMediator>());
    }

    [Fact]
    public void AddOpenBehavior_registers_open_generic_behaviors()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<Recorder>()
            .AddSwitchboard(cfg => cfg
                .RegisterServicesFromAssemblyContaining<RegistrationTests>()
                .AddOpenBehavior(typeof(FirstBehavior<,>))
                .AddOpenBehavior(typeof(SecondBehavior<,>)))
            .BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<Ping, string>>().ToList();

        Assert.Equal(2, behaviors.Count);
        Assert.IsType<FirstBehavior<Ping, string>>(behaviors[0]);
        Assert.IsType<SecondBehavior<Ping, string>>(behaviors[1]);
    }

    [Fact]
    public void AddOpenBehavior_rejects_types_that_are_not_open_generic_behaviors()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(
            () => services.AddSwitchboard(cfg => cfg.AddOpenBehavior(typeof(string))));
        Assert.Throws<ArgumentException>(
            () => services.AddSwitchboard(cfg => cfg.AddOpenBehavior(typeof(ShortCircuitBehavior))));
    }

    [Fact]
    public void AddSwitchboard_rejects_null_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddSwitchboard(null!));
        Assert.Throws<ArgumentNullException>(
            () => SwitchboardServiceCollectionExtensions.AddSwitchboard(null!, cfg => { }));
    }
}
