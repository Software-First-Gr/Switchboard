using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Switchboard.IntegrationTests;

public sealed class CancellationTests
{
    [Fact]
    public async Task Cancelling_a_gated_insert_disposes_its_scope_and_a_later_dispatch_can_write()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();
        using var cancellation = new CancellationTokenSource();

        host.Gate.Arm("INSERT");
        var canceledDispatch = circuit.Sender.Send(new CreateNote("canceled", "not saved"), cancellation.Token);
        await host.Gate.Held;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledDispatch);

        Assert.True(host.Contexts["canceled"].IsDisposed);
        Assert.Empty(await host.WithFreshContextAsync(db => db.Notes.AsNoTracking().ToListAsync()));

        var recoveredId = await circuit.Sender.Send(new CreateNote("recovered", "saved"));

        Assert.True(recoveredId > 0);
        Assert.True(host.Contexts["recovered"].IsDisposed);
        var notes = await host.WithFreshContextAsync(db => db.Notes.AsNoTracking().ToListAsync());
        var note = Assert.Single(notes);
        Assert.Equal("saved", note.Text);
    }
}