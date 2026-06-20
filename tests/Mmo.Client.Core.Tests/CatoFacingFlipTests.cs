using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S96 — unit tests for the pure Cato side-view flip rule (Godot-free). The sprite faces E/right unflipped:
// E/NE/SE -> normal, W/NW/SW -> flipped, N/S -> keep the last horizontal flip.
public sealed class CatoFacingFlipTests
{
    [Theory]
    [InlineData(Direction8.E)]
    [InlineData(Direction8.NE)]
    [InlineData(Direction8.SE)]
    public void RightFacings_AreNormal_RegardlessOfLast(Direction8 facing)
    {
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Theory]
    [InlineData(Direction8.W)]
    [InlineData(Direction8.NW)]
    [InlineData(Direction8.SW)]
    public void LeftFacings_AreFlipped_RegardlessOfLast(Direction8 facing)
    {
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Theory]
    [InlineData(Direction8.N)]
    [InlineData(Direction8.S)]
    public void VerticalFacings_KeepLast(Direction8 facing)
    {
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Fact]
    public void DefaultMappingIsNotInverted()
    {
        // Guards the InvertFlip switch: E (right) must read as unflipped by default.
        Assert.False(CatoFacingFlip.InvertFlip);
        Assert.False(CatoFacingFlip.Resolve(Direction8.E, lastFlipH: true));
    }
}
