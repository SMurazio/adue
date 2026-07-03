using Mmo.Client.Core;
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
}
