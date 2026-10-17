using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Switchboard.IntegrationTests;

/// <summary>
/// The classic Blazor streaming case: a component enumerates an EF Core query through <c>CreateStream</c> and, while
/// the stream is open, sends a command that writes.
/// </summary>
public sealed class StreamingTests
{
    private static async Task<TestHost> StartWithNotesAsync(Action<SwitchboardConfiguration>? configure = null)
    {
        var host = await TestHost.StartAsync(configure);

        await host.WithFreshContextAsync(db =>
        {
            db.Notes.AddRange(new Note { Text = "a" }, new Note { Text = "b" }, new Note { Text = "c" });
            return db.SaveChangesAsync();
        });

        return host;
    }

    private static Task<List<string>> AuditActions(TestHost host)
        => host.WithFreshContextAsync(db => db.AuditEntries.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());

    [Fact]
    public async Task With_scope_per_dispatch_a_write_sent_inside_await_foreach_gets_its_own_DbContext_and_the_streams_scope_ends_with_the_enumeration()
    {
        await using var host = await StartWithNotesAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        var streamed = new List<string>();

        await foreach (var note in circuit.Sender.CreateStream(new StreamNotes("stream")))
        {
            streamed.Add(note.Text);
            await circuit.Sender.Send(new WriteAudit($"write {note.Text}", $"seen {note.Text}"));

            // The stream's scope, and the reader on its DbContext, stay open across the writes.
            Assert.False(host.Contexts["stream"].IsDisposed);
            Assert.NotSame(host.Contexts["stream"], host.Contexts[$"write {note.Text}"]);
            Assert.True(host.Contexts[$"write {note.Text}"].IsDisposed);
        }

        Assert.Equal(new[] { "a", "b", "c" }, streamed);
        Assert.NotSame(circuit.DbContext, host.Contexts["stream"]);
        Assert.True(host.Contexts["stream"].IsDisposed);

        Assert.Equal(new[] { "seen a", "seen b", "seen c" }, await AuditActions(host));
    }

    /// <summary>
    /// Documents what actually happens in the sequential case without scope-per-dispatch. The write shares the stream's
    /// DbContext while its data reader is open, but nothing fails on SQLite: EF Core only holds its concurrency check
    /// for the duration of each <c>MoveNextAsync</c>, not between rows, and Microsoft.Data.Sqlite lets a connection
    /// run a command while a reader on it is open. Whether this case throws depends on the database provider; the
    /// overlap that EF Core itself rejects is the one below.
    /// </summary>
    [Fact]
    public async Task Without_scope_per_dispatch_a_write_awaited_inside_await_foreach_shares_the_streams_DbContext()
    {
        await using var host = await StartWithNotesAsync();
        await using var circuit = host.OpenCircuit();

        var streamed = new List<string>();

        await foreach (var note in circuit.Sender.CreateStream(new StreamNotes("stream")))
        {
            streamed.Add(note.Text);
            await circuit.Sender.Send(new WriteAudit($"write {note.Text}", $"seen {note.Text}"));

            Assert.Same(host.Contexts["stream"], host.Contexts[$"write {note.Text}"]);
        }

        Assert.Same(circuit.DbContext, host.Contexts["stream"]);
        Assert.Equal(new[] { "a", "b", "c" }, streamed);
        Assert.Equal(new[] { "seen a", "seen b", "seen c" }, await AuditActions(host));
    }

    /// <summary>
    /// A component is part-way through a stream when a click sends a save; the save is still in flight when the
    /// component asks for the next row. On the circuit's shared DbContext that is two operations at once.
    /// </summary>
    [Fact]
    public async Task Without_scope_per_dispatch_reading_the_next_row_while_a_save_is_in_flight_makes_EF_throw()
    {
        await using var host = await StartWithNotesAsync();
        await using var circuit = host.OpenCircuit();

        var stream = circuit.Sender.CreateStream(new StreamNotes("stream")).GetAsyncEnumerator();
        try
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal("a", stream.Current.Text);

            host.Gate.Arm("INSERT");
            var click = circuit.Sender.Send(new WriteAudit("click", "clicked"));
            await host.Gate.Held;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.MoveNextAsync().AsTask().Guarded());
            Assert.StartsWith(ConcurrentDispatchTests.SecondOperation, error.Message);
            Assert.Same(host.Contexts["stream"], host.Contexts["click"]);

            host.Gate.Release();
            await click.Guarded();
        }
        finally
        {
            await stream.DisposeAsync();
        }

        Assert.Equal(new[] { "clicked" }, await AuditActions(host));
    }

    [Fact]
    public async Task With_scope_per_dispatch_reading_the_next_row_while_a_save_is_in_flight_works_and_abandoning_the_stream_disposes_its_scope()
    {
        await using var host = await StartWithNotesAsync(cfg => cfg.UseScopePerDispatch());
        await using var circuit = host.OpenCircuit();

        var stream = circuit.Sender.CreateStream(new StreamNotes("stream")).GetAsyncEnumerator();
        try
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal("a", stream.Current.Text);

            host.Gate.Arm("INSERT");
            var click = circuit.Sender.Send(new WriteAudit("click", "clicked"));
            await host.Gate.Held;

            Assert.True(await stream.MoveNextAsync().AsTask().Guarded());
            Assert.Equal("b", stream.Current.Text);
            Assert.False(click.IsCompleted);

            host.Gate.Release();
            await click.Guarded();

            Assert.NotSame(host.Contexts["stream"], host.Contexts["click"]);
            Assert.False(host.Contexts["stream"].IsDisposed);
        }
        finally
        {
            // Stop after two of the three rows, as breaking out of await foreach would.
            await stream.DisposeAsync();
        }

        Assert.True(host.Contexts["stream"].IsDisposed);
        Assert.Equal(new[] { "clicked" }, await AuditActions(host));
    }
}
