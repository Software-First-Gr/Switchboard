using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Tests;

/// <summary>Thread-safe recorder used to observe execution order across handlers and behaviors.</summary>
public sealed class Recorder
{
    private readonly object _gate = new();
    private readonly List<string> _entries = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public string Joined => string.Join("|", Entries);

    public void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

// --- Shared request/handler fixtures ---------------------------------------

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
        => Task.FromResult(request.Message + " pong");
}

public sealed record FireAndForget : IRequest;

public sealed class FireAndForgetHandler : IRequestHandler<FireAndForget>
{
    private readonly Recorder _recorder;

    public FireAndForgetHandler(Recorder recorder) => _recorder = recorder;

    public Task Handle(FireAndForget request, CancellationToken cancellationToken)
    {
        _recorder.Add("fire-and-forget");
        return Task.CompletedTask;
    }
}

/// <summary>A request with no registered handler, on purpose.</summary>
public sealed record Orphan : IRequest<int>;

/// <summary>Abstract handler that assembly scanning must skip.</summary>
public abstract class AbstractPingHandler : IRequestHandler<Ping, string>
{
    public abstract Task<string> Handle(Ping request, CancellationToken cancellationToken);
}

/// <summary>Open generic handler that assembly scanning must skip.</summary>
public sealed class OpenGenericPingHandler<T> : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
        => Task.FromResult("open-generic");
}
