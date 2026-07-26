using System;
using System.Threading.Tasks;

namespace Switchboard;

/// <summary>Represents a void response, equivalent to MediatR's <c>Unit</c>.</summary>
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    /// <summary>The single <see cref="Unit"/> value.</summary>
    public static readonly Unit Value = new();

    /// <summary>A cached completed task that returns <see cref="Value"/>.</summary>
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);

    /// <summary>Always returns 0; all <see cref="Unit"/> values are equal.</summary>
    public int CompareTo(Unit other) => 0;

    int IComparable.CompareTo(object? obj) => 0;

    /// <summary>Always returns <see langword="true"/>; all <see cref="Unit"/> values are equal.</summary>
    public bool Equals(Unit other) => true;

    /// <summary>Returns <see langword="true"/> when <paramref name="obj"/> is a <see cref="Unit"/>.</summary>
    public override bool Equals(object? obj) => obj is Unit;

    /// <summary>Always returns 0.</summary>
    public override int GetHashCode() => 0;

    /// <summary>Always returns <see langword="true"/>.</summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>Always returns <see langword="false"/>.</summary>
    public static bool operator !=(Unit left, Unit right) => false;

    /// <summary>Returns <c>"()"</c>.</summary>
    public override string ToString() => "()";
}
