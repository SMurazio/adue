using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// M2 perf (docs/town-floor1-blockout-design.md): pins the packed MultiMesh buffer layout the Godot side
// uploads wholesale via `MultiMesh.Buffer = ...` — Godot 4's 3D-transform format, 12 floats per instance,
// basis rows interleaved with the origin. A layout regression here would render as a silently garbled
// floor/wall set, so the exact float order is asserted headlessly.
public sealed class MultiMeshTileBufferTests
{
    [Fact]
    public void PacksTwelveFloatsPerInstanceIdentityBasisRowMajor()
    {
        var buffer = MultiMeshTileBuffer.PackUprightTileTransforms(
            [new TileCoord(3, 7), new TileCoord(11, 2)], y: 0.4f);

        Assert.Equal(2 * MultiMeshTileBuffer.FloatsPerInstance, buffer.Length);

        // Instance 0 at (3, 0.4, 7): rows (1,0,0 | 0,1,0 | 0,0,1) each followed by its origin component.
        Assert.Equal(
            new float[] { 1f, 0f, 0f, 3f, 0f, 1f, 0f, 0.4f, 0f, 0f, 1f, 7f },
            buffer[..MultiMeshTileBuffer.FloatsPerInstance]);

        // Instance 1 at (11, 0.4, 2), immediately after — no padding between instances.
        Assert.Equal(
            new float[] { 1f, 0f, 0f, 11f, 0f, 1f, 0f, 0.4f, 0f, 0f, 1f, 2f },
            buffer[MultiMeshTileBuffer.FloatsPerInstance..]);
    }

    [Fact]
    public void PacksEmptyTileListAsEmptyBuffer()
    {
        Assert.Empty(MultiMeshTileBuffer.PackUprightTileTransforms([], y: 0.03f));
    }

    // PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1): PackDecorTransforms extends
    // the same 12-floats-per-instance layout with a real (Y-rotation + uniform scale) basis instead of
    // identity. Zero rotation / unit scale must degenerate to exactly the identity-basis shape
    // PackUprightTileTransforms already produces (minus the sub-tile X/Z offset, which is zero here too).
    [Fact]
    public void PackDecorTransforms_ZeroRotationUnitScale_IsIdentityBasis()
    {
        var buffer = MultiMeshTileBuffer.PackDecorTransforms(
            [new DecorPlacer.DecorInstance(X: 3f, Z: 7f, RotationRadians: 0f, Scale: 1f)],
            groundY: 0.4f);

        Assert.Equal(
            new float[] { 1f, 0f, 0f, 3f, 0f, 1f, 0f, 0.4f, 0f, 0f, 1f, 7f },
            buffer);
    }

    [Fact]
    public void PackDecorTransforms_AppliesRotationAndUniformScale()
    {
        // A 90-degree rotation about Y with scale 2: cos(90)=0, sin(90)=1, so
        // row0 = (0, 0, 2), row1 = (0, 2, 0), row2 = (-2, 0, 0) -- see PackDecorTransforms' own doc
        // comment for the Basis(Vector3.Up, angle) row convention this matches.
        var buffer = MultiMeshTileBuffer.PackDecorTransforms(
            [new DecorPlacer.DecorInstance(X: 11f, Z: 2f, RotationRadians: MathF.PI / 2f, Scale: 2f)],
            groundY: 0.032f);

        Assert.Equal(0f, buffer[0], precision: 5);
        Assert.Equal(0f, buffer[1], precision: 5);
        Assert.Equal(2f, buffer[2], precision: 5);
        Assert.Equal(11f, buffer[3], precision: 5);
        Assert.Equal(0f, buffer[4], precision: 5);
        Assert.Equal(2f, buffer[5], precision: 5);
        Assert.Equal(0f, buffer[6], precision: 5);
        Assert.Equal(0.032f, buffer[7], precision: 5);
        Assert.Equal(-2f, buffer[8], precision: 5);
        Assert.Equal(0f, buffer[9], precision: 5);
        Assert.Equal(0f, buffer[10], precision: 5);
        Assert.Equal(2f, buffer[11], precision: 5);
    }

    [Fact]
    public void PackDecorTransforms_EmptyList_PacksEmptyBuffer()
    {
        Assert.Empty(MultiMeshTileBuffer.PackDecorTransforms([], groundY: 0.03f));
    }
}
