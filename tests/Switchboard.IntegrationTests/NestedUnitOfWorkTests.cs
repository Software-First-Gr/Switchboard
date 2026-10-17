using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Switchboard.IntegrationTests;

/// <summary>
/// With scope-per-dispatch on, a handler that sends or publishes again through its injected sender or publisher
/// stays in the dispatch in flight: one DbContext, one unit of work, one transaction.
/// </summary>
public sealed class NestedUnitOfWorkTests
{
    [Fact]
    public async Task A_nested_send_shares_the_outer_handlers_DbContext()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        var noteId = await circuit.Sender.Send(new CreateNoteWithAudit("with audit"));

        Assert.Same(host.Contexts["outer"], host.Contexts["inner"]);
        Assert.NotSame(circuit.DbContext, host.Contexts["outer"]);
        Assert.True(host.Contexts["outer"].IsDisposed);

        Assert.Equal(new[] { "with audit" }, await host.WithFreshContextAsync(db => db.Notes.Select(n => n.Text).ToListAsync()));
        Assert.Equal(
            new[] { $"created note {noteId}" },
            await host.WithFreshContextAsync(db => db.AuditEntries.Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task A_nested_send_writes_in_the_outer_handlers_transaction_and_rolls_back_with_it()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        var (outer, inner) = await circuit.Sender.Send(new CreateNoteThenRollBack("rolled back"));

        // The nested handler's SaveChangesAsync ran inside the transaction the outer handler opened.
        Assert.Equal(outer, inner);

        Assert.Empty(await host.WithFreshContextAsync(db => db.Notes.ToListAsync()));
        Assert.Empty(await host.WithFreshContextAsync(db => db.AuditEntries.ToListAsync()));
    }

    [Fact]
    public async Task A_domain_event_published_from_a_handler_shares_its_DbContext()
    {
        await using var host = await TestHost.StartAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        var noteId = await circuit.Sender.Send(new CreateNoteAndPublish("with event"));

        Assert.Same(host.Contexts["outer"], host.Contexts["event"]);
        Assert.Equal(
            new[] { $"note {noteId} created" },
            await host.WithFreshContextAsync(db => db.AuditEntries.Select(a => a.Action).ToListAsync()));
    }
}
