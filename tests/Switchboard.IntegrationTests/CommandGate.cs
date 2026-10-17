using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Switchboard.IntegrationTests;

/// <summary>
/// Holds one database command just before it executes, so a dispatch is provably mid-operation — inside EF Core's
/// critical section for that DbContext — while the test starts another one. Microsoft.Data.Sqlite completes its
/// async calls synchronously, so without this two "concurrent" dispatches would never actually overlap.
/// </summary>
/// <remarks>
/// <see cref="Arm"/> it, start the first dispatch, await <see cref="Held"/>, start the second, then <see cref="Release"/>.
/// Only the first matching command after arming is held; every other command passes straight through.
/// </remarks>
public sealed class CommandGate
{
    // Guards against a hang if a test is wrong; never part of what a test asserts.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<string> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _commandPrefix;
    private int _armed;

    /// <summary>Completes, with the command's text, once a command is being held.</summary>
    public Task<string> Held => _held.Task.WaitAsync(HangGuard);

    /// <summary>Holds the next command whose SQL starts with <paramref name="commandPrefix"/> (e.g. INSERT, SELECT).</summary>
    public void Arm(string commandPrefix)
    {
        _commandPrefix = commandPrefix;
        Volatile.Write(ref _armed, 1);
    }

    /// <summary>Lets the held command execute.</summary>
    public void Release() => _released.TrySetResult();

    internal async ValueTask WaitIfArmedAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _armed) == 0 ||
            !command.CommandText.TrimStart().StartsWith(_commandPrefix!, StringComparison.OrdinalIgnoreCase) ||
            Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
        {
            return;
        }

        _held.TrySetResult(command.CommandText);
        await _released.Task.WaitAsync(HangGuard, cancellationToken);
    }
}

/// <summary>Routes every async command through the <see cref="CommandGate"/>.</summary>
public sealed class GatingInterceptor : DbCommandInterceptor
{
    private readonly CommandGate _gate;

    public GatingInterceptor(CommandGate gate) => _gate = gate;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await _gate.WaitIfArmedAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await _gate.WaitIfArmedAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await _gate.WaitIfArmedAsync(command, cancellationToken);
        return result;
    }
}
