using System.Threading.Tasks;

namespace Switchboard.Benchmarks.Study;

/// <summary>
/// A deterministic suspension point. Without RunContinuationsAsynchronously, <see cref="Release"/> runs every awaiting
/// continuation inline, so a suspended dispatch resumes and completes before Release returns: the benchmark measures the
/// cost of suspending and resuming the async frames, not thread-pool scheduling.
/// </summary>
public sealed class Gate
{
    private TaskCompletionSource? _current;

    /// <summary>What the handler awaits: an incomplete task when <see cref="Arm"/> was called, else a completed one.</summary>
    public Task Wait => _current?.Task ?? Task.CompletedTask;

    public void Arm() => _current = new TaskCompletionSource();

    public void Release()
    {
        var current = _current!;
        _current = null;
        current.SetResult();
    }
}
