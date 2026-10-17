using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Switchboard.IntegrationTests;

/// <summary>Saves a note stamped with the current user. <paramref name="Label"/> names the dispatch in the <see cref="ContextLog"/>.</summary>
public sealed record CreateNote(string Label, string Text) : IRequest<int>;

public sealed class CreateNoteHandler : IRequestHandler<CreateNote, int>
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;
    private readonly ContextLog _contexts;

    public CreateNoteHandler(AppDbContext db, CurrentUser currentUser, ContextLog contexts)
    {
        _db = db;
        _currentUser = currentUser;
        _contexts = contexts;
    }

    public async Task<int> Handle(CreateNote request, CancellationToken cancellationToken)
    {
        _contexts.Record(request.Label, _db);

        var note = new Note { Text = request.Text, CreatedBy = _currentUser.Name };
        _db.Notes.Add(note);
        await _db.SaveChangesAsync(cancellationToken);

        return note.Id;
    }
}

/// <summary>Writes an audit entry and answers with the id of the transaction it was written in, if any.</summary>
public sealed record WriteAudit(string Label, string Action) : IRequest<Guid?>;

public sealed class WriteAuditHandler : IRequestHandler<WriteAudit, Guid?>
{
    private readonly AppDbContext _db;
    private readonly ContextLog _contexts;

    public WriteAuditHandler(AppDbContext db, ContextLog contexts)
    {
        _db = db;
        _contexts = contexts;
    }

    public async Task<Guid?> Handle(WriteAudit request, CancellationToken cancellationToken)
    {
        _contexts.Record(request.Label, _db);

        _db.AuditEntries.Add(new AuditEntry { Action = request.Action });
        await _db.SaveChangesAsync(cancellationToken);

        return _db.Database.CurrentTransaction?.TransactionId;
    }
}

/// <summary>Saves a note, then sends a nested <see cref="WriteAudit"/> through its injected sender.</summary>
public sealed record CreateNoteWithAudit(string Text) : IRequest<int>;

public sealed class CreateNoteWithAuditHandler : IRequestHandler<CreateNoteWithAudit, int>
{
    private readonly AppDbContext _db;
    private readonly ISender _sender;
    private readonly ContextLog _contexts;

    public CreateNoteWithAuditHandler(AppDbContext db, ISender sender, ContextLog contexts)
    {
        _db = db;
        _sender = sender;
        _contexts = contexts;
    }

    public async Task<int> Handle(CreateNoteWithAudit request, CancellationToken cancellationToken)
    {
        _contexts.Record("outer", _db);

        var note = new Note { Text = request.Text };
        _db.Notes.Add(note);
        await _db.SaveChangesAsync(cancellationToken);

        await _sender.Send(new WriteAudit("inner", $"created note {note.Id}"), cancellationToken);

        return note.Id;
    }
}

/// <summary>
/// Opens a transaction, saves a note, sends a nested <see cref="WriteAudit"/>, then rolls everything back.
/// Answers with the transaction it opened and the one the nested handler saw.
/// </summary>
public sealed record CreateNoteThenRollBack(string Text) : IRequest<(Guid Outer, Guid? Inner)>;

public sealed class CreateNoteThenRollBackHandler : IRequestHandler<CreateNoteThenRollBack, (Guid Outer, Guid? Inner)>
{
    private readonly AppDbContext _db;
    private readonly ISender _sender;

    public CreateNoteThenRollBackHandler(AppDbContext db, ISender sender)
    {
        _db = db;
        _sender = sender;
    }

    public async Task<(Guid Outer, Guid? Inner)> Handle(CreateNoteThenRollBack request, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        _db.Notes.Add(new Note { Text = request.Text });
        await _db.SaveChangesAsync(cancellationToken);

        var inner = await _sender.Send(new WriteAudit("inner", "written inside the outer transaction"), cancellationToken);

        await transaction.RollbackAsync(cancellationToken);

        return (transaction.TransactionId, inner);
    }
}

/// <summary>A domain event, published by <see cref="CreateNoteAndPublishHandler"/> after it saves.</summary>
public sealed record NoteCreated(int NoteId) : INotification;

public sealed class NoteCreatedAuditHandler : INotificationHandler<NoteCreated>
{
    private readonly AppDbContext _db;
    private readonly ContextLog _contexts;

    public NoteCreatedAuditHandler(AppDbContext db, ContextLog contexts)
    {
        _db = db;
        _contexts = contexts;
    }

    public async Task Handle(NoteCreated notification, CancellationToken cancellationToken)
    {
        _contexts.Record("event", _db);

        _db.AuditEntries.Add(new AuditEntry { Action = $"note {notification.NoteId} created" });
        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed record CreateNoteAndPublish(string Text) : IRequest<int>;

public sealed class CreateNoteAndPublishHandler : IRequestHandler<CreateNoteAndPublish, int>
{
    private readonly AppDbContext _db;
    private readonly IPublisher _publisher;
    private readonly ContextLog _contexts;

    public CreateNoteAndPublishHandler(AppDbContext db, IPublisher publisher, ContextLog contexts)
    {
        _db = db;
        _publisher = publisher;
        _contexts = contexts;
    }

    public async Task<int> Handle(CreateNoteAndPublish request, CancellationToken cancellationToken)
    {
        _contexts.Record("outer", _db);

        var note = new Note { Text = request.Text };
        _db.Notes.Add(note);
        await _db.SaveChangesAsync(cancellationToken);

        await _publisher.Publish(new NoteCreated(note.Id), cancellationToken);

        return note.Id;
    }
}

/// <summary>Streams every note straight off an EF Core query, as the README's <c>GetOrderLines</c> example does.</summary>
public sealed record StreamNotes(string Label) : IStreamRequest<Note>;

public sealed class StreamNotesHandler : IStreamRequestHandler<StreamNotes, Note>
{
    private readonly AppDbContext _db;
    private readonly ContextLog _contexts;

    public StreamNotesHandler(AppDbContext db, ContextLog contexts)
    {
        _db = db;
        _contexts = contexts;
    }

    public IAsyncEnumerable<Note> Handle(StreamNotes request, CancellationToken cancellationToken)
    {
        _contexts.Record(request.Label, _db);

        return _db.Notes.OrderBy(n => n.Id).AsAsyncEnumerable();
    }
}
