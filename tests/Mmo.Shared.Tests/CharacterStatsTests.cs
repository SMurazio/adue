using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// COMBAT-S1: the vitals value type's clamp logic — the unit the server setter (WorldEntity.TrySetStatCurrent) and
// the dev-set window both rely on. No damage/regen here; only the [0, max] clamp on a current value.
public class CharacterStatsTests
{
    [Fact]
    public void DefaultIsFullHundredEach()
    {
        var stats = CharacterStats.Default;

        Assert.Equal(100, stats.Health);
        Assert.Equal(100, stats.MaxHealth);
        Assert.Equal(100, stats.Mana);
        Assert.Equal(100, stats.MaxMana);
        Assert.Equal(100, stats.Stamina);
        Assert.Equal(100, stats.MaxStamina);
    }

    [Theory]
    [InlineData(50, 50)]   // in range
    [InlineData(-10, 0)]   // below floor -> 0
    [InlineData(250, 100)] // above max -> max
    [InlineData(0, 0)]     // floor edge
    [InlineData(100, 100)] // max edge
    public void WithHealthClampsToZeroToMax(int requested, int expected)
    {
        var stats = CharacterStats.Default.WithHealth(requested);

        Assert.Equal(expected, stats.Health);
        // Max and the other vitals are untouched by a current-value set.
        Assert.Equal(100, stats.MaxHealth);
        Assert.Equal(100, stats.Mana);
        Assert.Equal(100, stats.Stamina);
    }

    [Fact]
    public void WithManaAndWithStaminaClampIndependently()
    {
        var stats = CharacterStats.Default.WithMana(999).WithStamina(-5);

        Assert.Equal(100, stats.Mana);   // clamped up to max
        Assert.Equal(0, stats.Stamina);  // clamped down to 0
        Assert.Equal(100, stats.Health); // unchanged
    }

    [Fact]
    public void NonPositiveMaxYieldsZero()
    {
        var degenerate = new CharacterStats(50, 0, 50, -1, 50, 100);

        // A degenerate (<=0) max clamps any current value to 0.
        Assert.Equal(0, degenerate.WithHealth(50).Health);
        Assert.Equal(0, degenerate.WithMana(50).Mana);
        // A valid max still clamps normally.
        Assert.Equal(80, degenerate.WithStamina(80).Stamina);
    }
}
