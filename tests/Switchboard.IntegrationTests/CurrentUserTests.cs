using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Switchboard.IntegrationTests;

/// <summary>
/// A dispatch scope is new, so scoped state from the circuit — who is signed in — has to be carried into it by the
/// <c>UseScopePerDispatch</c> callback. Mirrors the README's <c>AuthenticationStateProvider</c> example.
/// </summary>
public sealed class CurrentUserTests
{
    private static SwitchboardConfiguration CarryCurrentUser(SwitchboardConfiguration cfg)
        => cfg.UseScopePerDispatch(async (scope, cancellationToken) =>
        {
            var auth = await scope.Parent.GetRequiredService<CircuitAuthenticationState>().GetAuthenticationStateAsync();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(auth);
        });

    private static Task<string?> CreatedBy(TestHost host, int noteId)
        => host.WithFreshContextAsync(db => db.Notes.Where(n => n.Id == noteId).Select(n => n.CreatedBy).SingleAsync());

    [Fact]
    public async Task The_callback_carries_the_circuits_user_into_the_dispatch_and_the_handler_stamps_it()
    {
        await using var host = await TestHost.StartAsync(cfg => CarryCurrentUser(cfg));
        await using var circuit = host.OpenCircuit("alice");

        var noteId = await circuit.Sender.Send(new CreateNote("alice's", "stamped"));

        Assert.Equal("alice", await CreatedBy(host, noteId));
    }

    [Fact]
    public async Task The_synchronous_callback_carries_the_user_too()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch(scope =>
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Set(
                scope.Parent.GetRequiredService<CircuitAuthenticationState>().User)));
        await using var circuit = host.OpenCircuit("alice");

        var noteId = await circuit.Sender.Send(new CreateNote("alice's", "stamped"));

        Assert.Equal("alice", await CreatedBy(host, noteId));
    }

    [Fact]
    public async Task Overlapping_dispatches_from_two_circuits_each_stamp_their_own_user()
    {
        await using var host = await TestHost.StartAsync(cfg => CarryCurrentUser(cfg));
        await using var alice = host.OpenCircuit("alice");
        await using var bob = host.OpenCircuit("bob");

        host.Gate.Arm("INSERT");
        var alicesSave = alice.Sender.Send(new CreateNote("alice's", "by alice"));
        await host.Gate.Held;

        var bobsNote = await bob.Sender.Send(new CreateNote("bob's", "by bob")).Guarded();

        host.Gate.Release();
        var alicesNote = await alicesSave.Guarded();

        Assert.Equal("alice", await CreatedBy(host, alicesNote));
        Assert.Equal("bob", await CreatedBy(host, bobsNote));
    }

    [Fact]
    public async Task Without_the_callback_the_dispatch_scope_has_no_user()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit("alice");

        var noteId = await circuit.Sender.Send(new CreateNote("alice's", "not stamped"));

        // The circuit's CurrentUser knows alice, but the handler got the dispatch scope's fresh, empty one.
        Assert.Equal("alice", circuit.CurrentUser.Name);
        Assert.Null(await CreatedBy(host, noteId));
    }

    [Fact]
    public async Task Without_scope_per_dispatch_the_handler_sees_the_circuits_user_directly()
    {
        await using var host = await TestHost.StartAsync();
        await using var circuit = host.OpenCircuit("alice");

        var noteId = await circuit.Sender.Send(new CreateNote("alice's", "stamped"));

        Assert.Equal("alice", await CreatedBy(host, noteId));
    }
}
