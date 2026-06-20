using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S100 — unit tests for the pure Cato side-view flip rule (Godot-free), now CAMERA-relative. The fixed iso
// camera's screen-right axis is world tile (1, -1), so screenH = delta.X - delta.Y:
//   N/E/NE (screenH > 0) -> normal; S/W/SW (screenH < 0) -> flipped; NW/SE (screenH == 0) -> keep last flip.
public sealed class CatoFacingFlipTests
{
    [Theory]
    [InlineData(Direction8.N)]
    [InlineData(Direction8.E)]
    [InlineData(Direction8.NE)]
    public void ScreenRightFacings_AreNormal_RegardlessOfLast(Direction8 facing)
    {
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Theory]
    [InlineData(Direction8.S)]
    [InlineData(Direction8.W)]
    [InlineData(Direction8.SW)]
    public void ScreenLeftFacings_AreFlipped_RegardlessOfLast(Direction8 facing)
    {
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Theory]
    [InlineData(Direction8.NW)] // screen-up
    [InlineData(Direction8.SE)] // screen-down
    public void ScreenVerticalFacings_KeepLast(Direction8 facing)
    {
        Assert.False(CatoFacingFlip.Resolve(facing, lastFlipH: false));
        Assert.True(CatoFacingFlip.Resolve(facing, lastFlipH: true));
    }

    [Fact]
    public void UserRepro_FacingSouthFromPriorRight_BecomesFlipped()
    {
        // Move screen-down-left (world S) while previously facing screen-right (not flipped):
        // Cato must now face screen-left (flipped). This is the exact user-repro that the world-X rule missed.
        Assert.True(CatoFacingFlip.Resolve(Direction8.S, lastFlipH: false));
    }

    [Fact]
    public void UserRepro_FacingNorthFromPriorLeft_BecomesNormal()
    {
        // Move screen-up-right (world N) while previously flipped: Cato must turn back to screen-right (normal).
        Assert.False(CatoFacingFlip.Resolve(Direction8.N, lastFlipH: true));
    }

    [Fact]
    public void DefaultMappingIsNotInverted()
    {
        // Guards the InvertFlip switch: a screen-right facing (e.g. E) must read as unflipped by default.
        Assert.False(CatoFacingFlip.InvertFlip);
        Assert.False(CatoFacingFlip.Resolve(Direction8.E, lastFlipH: true));
    }
}
