using Mmo.Client.Core;
using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// Server↔client parity: the server's authoritative Zone (generated from a seed) and the client's
// ZoneModel (regenerated from the same seed descriptor in ZoneInfo) MUST produce the identical map.
public sealed class TerrainParityTests
{
    [Theory]
    [InlineData(64, 64, 0)]
    [InlineData(128, 128, 0)]
    [InlineData(256, 256, 99)]
    public void ServerZoneAndClientZoneModelAgree(int width, int height, int seed)
    {
        var zone = Zone.CreateGenerated(width, height, seed, TerrainGenerator.CurrentGenVersion, SpawnDistribution.Clustered);

        var model = new ZoneModel(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        // Same blocked set (order-independent set comparison).
        Assert.True(zone.BlockedTiles.SetEquals(model.BlockedTiles));

        // Same content hash, computed independently on each side.
        var serverHash = TerrainGenerator.ContentHash(width, height, seed, TerrainGenerator.CurrentGenVersion);
        Assert.Equal(serverHash, model.ContentHash);
    }

    [Fact]
    public void ZoneInfoMessageHashMatchesClientRegeneration()
    {
        // Build the descriptor the server would put on the wire, then regenerate on the client and
        // confirm the hash matches (the drift/tamper gate).
        var zone = Zone.CreateGenerated(128, 128, 7, TerrainGenerator.CurrentGenVersion, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
        var message = new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash);

        var model = new ZoneModel(message.ZoneId, message.Width, message.Height, message.Seed, message.GenVersion);

        Assert.Equal(message.ContentHash, model.ContentHash);
    }
}
