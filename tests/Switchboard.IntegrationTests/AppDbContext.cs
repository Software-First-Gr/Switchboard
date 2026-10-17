using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Switchboard.IntegrationTests;

public sealed class Note
{
    public int Id { get; set; }

    public string Text { get; set; } = "";

    public string? CreatedBy { get; set; }
}

public sealed class AuditEntry
{
    public int Id { get; set; }

    public string Action { get; set; } = "";
}

/// <summary>The application's DbContext, registered scoped with <c>AddDbContext</c> as a Blazor Server app would.</summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>Set once the owning DI scope has disposed this instance.</summary>
    public bool IsDisposed { get; private set; }

    public override void Dispose()
    {
        base.Dispose();
        IsDisposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        IsDisposed = true;
    }
}
