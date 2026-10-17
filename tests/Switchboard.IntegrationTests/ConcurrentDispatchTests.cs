using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Switchboard.IntegrationTests;

/// <summary>
/// The README's headline case: two dispatches from one Blazor Server circuit overlap — a click while a save is still
/// in flight. The circuit's scope is the whole connection, so without scope-per-dispatch both share its DbContext.
/// </summary>
public sealed class ConcurrentDispatchTests
{
    /// <summary>The first sentence of EF Core's <c>ConcurrentMethodInvocation</c> error.</summary>
    internal const string SecondOperation =
        "A second operation was started on this context instance before a previous operation completed.";

    [Fact]
    public async Task Without_scope_per_dispatch_overlapping_sends_share_the_circuits_DbContext_and_EF_throws()
    {
        await using var host = await TestHost.StartAsync();
        await using var circuit = host.OpenCircuit();

        host.Gate.Arm("INSERT");
        var first = circuit.Sender.Send(new CreateNote("first", "saved first"));
        var heldCommand = await host.Gate.Held;

        // The first dispatch is now inside SaveChangesAsync, its INSERT held just before it executes.
        Assert.False(first.IsCompleted);
        Assert.StartsWith("INSERT INTO \"Notes\"", heldCommand);

        var second = circuit.Sender.Send(new CreateNote("second", "saved second"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Guarded());
        Assert.StartsWith(SecondOperation, error.Message);

        // Both handlers were handed the circuit's one DbContext.
        Assert.Same(circuit.DbContext, host.Contexts["first"]);
        Assert.Same(circuit.DbContext, host.Contexts["second"]);

        host.Gate.Release();
        await first.Guarded();

        var saved = await host.WithFreshContextAsync(db => db.Notes.Select(n => n.Text).ToListAsync());
        Assert.Equal(new[] { "saved first" }, saved);
    }

    [Fact]
    public async Task With_scope_per_dispatch_overlapping_sends_each_get_their_own_DbContext_and_both_save()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        host.Gate.Arm("INSERT");
        var first = circuit.Sender.Send(new CreateNote("first", "saved first"));
        await host.Gate.Held;

        // The same overlap as above: the first dispatch is mid-SaveChangesAsync while the second starts and finishes.
        Assert.False(first.IsCompleted);

        var secondId = await circuit.Sender.Send(new CreateNote("second", "saved second")).Guarded();
        Assert.False(first.IsCompleted);

        host.Gate.Release();
        var firstId = await first.Guarded();

        var firstDb = host.Contexts["first"];
        var secondDb = host.Contexts["second"];
        Assert.NotSame(firstDb, secondDb);
        Assert.NotSame(circuit.DbContext, firstDb);
        Assert.NotSame(circuit.DbContext, secondDb);

        // Each dispatch scope, and its DbContext, ended with its dispatch.
        Assert.True(firstDb.IsDisposed);
        Assert.True(secondDb.IsDisposed);
        Assert.False(circuit.DbContext.IsDisposed);

        var saved = await host.WithFreshContextAsync(db => db.Notes.OrderBy(n => n.Id).Select(n => new { n.Id, n.Text }).ToListAsync());
        Assert.Equal(
            new[] { new { Id = secondId, Text = "saved second" }, new { Id = firstId, Text = "saved first" } },
            saved);
    }
}
