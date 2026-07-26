using System;
using System.Threading.Tasks;
using Xunit;

namespace Switchboard.Tests;

public sealed class UnitStructTests
{
    [Fact]
    public void All_unit_values_are_equal()
    {
        Assert.True(Unit.Value == new Unit());
        Assert.False(Unit.Value != new Unit());
        Assert.True(Unit.Value.Equals(new Unit()));
        Assert.True(((object)Unit.Value).Equals(new Unit()));
        Assert.False(Unit.Value.Equals("not a unit"));
    }

    [Fact]
    public void Unit_hash_code_and_comparisons_are_stable()
    {
        Assert.Equal(0, Unit.Value.GetHashCode());
        Assert.Equal(0, Unit.Value.CompareTo(new Unit()));
        Assert.Equal(0, ((IComparable)Unit.Value).CompareTo(new Unit()));
    }

    [Fact]
    public void Unit_prints_as_empty_tuple()
    {
        Assert.Equal("()", Unit.Value.ToString());
    }

    [Fact]
    public async Task Unit_task_is_completed_and_returns_value()
    {
        Assert.True(Unit.Task.IsCompletedSuccessfully);
        Assert.Equal(Unit.Value, await Unit.Task);
    }
}
