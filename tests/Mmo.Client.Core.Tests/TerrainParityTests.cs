using Mmo.Client.Core;
using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// Server↔client parity: the server's authoritative Zone (generated from a seed) and the client's
// ZoneModel (regenerated from the same seed descriptor in ZoneInfo) MUST produce the identical map.
public sealed class TerrainParityTests
{
    [Theory]
    [InlineData(64, 64, 0, 1)]
    [InlineData(128, 128, 0, 1)]
    [InlineData(256, 256, 99, 1)]
    // AUTHORED-MAP M3: the live default — the authored map at its intrinsic dims (seed unused).
    [InlineData(AuthoredMaps.TownAndFloor1Width, AuthoredMaps.TownAndFloor1Height, 0, TerrainGenerator.AuthoredGenVersion)]
    public void ServerZoneAndClientZoneModelAgree(int width, int height, int seed, int genVersion)
    {
        var zone = Zone.CreateGenerated(width, height, seed, genVersion, SpawnDistribution.Clustered);

        var model = new ZoneModel(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion);

        // Same blocked set (order-independent set comparison).
        Assert.True(zone.BlockedTiles.SetEquals(model.BlockedTiles));

        // Same content hash, computed independently on each side.
        var serverHash = TerrainGenerator.ContentHash(width, height, seed, genVersion);
        Assert.Equal(serverHash, model.ContentHash);
    }

    [Fact]
    public void ZoneInfoMessageHashMatchesClientRegeneration()
    {
        // Build the descriptor the server would put on the wire, then regenerate on the client and
        // confirm the hash matches (the drift/tamper gate). genVersion 1: the procedural path at an
        // arbitrary size (the authored path is covered by the theory case above).
        var zone = Zone.CreateGenerated(128, 128, 7, 1, SpawnDistribution.Clustered);
        var serverHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
        // NODE-FIELD N2: genVersion 1 (procedural) has no authored map to scatter from — the trivial empty
        // catalogue's hash.
        var message = new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, serverHash, NodeCatalog.Empty().CatalogHash);

        var model = new ZoneModel(message.ZoneId, message.Width, message.Height, message.Seed, message.GenVersion);

        Assert.Equal(message.ContentHash, model.ContentHash);
    }

    // NODE-FIELD N2 (D2): the unit-level pin on MmoClient's CatalogHash compare — a mismatch (drifted/tampered
    // scatter code) must be a LOUD diagnostic ClientError, mirroring the ContentHash mismatch's behavior
    // EXACTLY: the client still applies the regenerated zone (NOT a connection-level hard-fail), because the
    // server stays authoritative for the actual harvest regardless of what a drifted client renders.
    [Fact]
    public void ZoneInfoNodeCatalogHashMismatchIsALoudDiagnosticNotAHardFail()
    {
        using var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        var zone = Zone.CreateGenerated(128, 128, 3, 1, SpawnDistribution.Clustered);
        var contentHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
        const ulong wrongCatalogHash = 0xBADC0FFEEBADC0DEUL;

        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, contentHash, wrongCatalogHash));

        Assert.Contains(client.Errors, e => e.Code == "node-catalog-hash-mismatch");
        // Still applies the zone (loud, not a hard fail) — Zone is set regardless of the mismatch.
        Assert.NotNull(client.Zone);
    }

    // The matching HAPPY-PATH pin: when the client independently builds the SAME catalogue the server did
    // (genVersion 1 here has no authored data, so both sides agree on the trivial empty catalogue), no error
    // is raised.
    [Fact]
    public void ZoneInfoNodeCatalogHashMatchesForProceduralZoneRaisesNoError()
    {
        using var client = new MmoClient(new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"));
        var zone = Zone.CreateGenerated(128, 128, 3, 1, SpawnDistribution.Clustered);
        var contentHash = TerrainGenerator.ContentHash(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
        var catalogHash = NodeCatalog.Empty().CatalogHash;

        client.HandleMessageForTests(new ZoneInfoMessage(zone.Id, zone.Width, zone.Height, zone.Seed, zone.GenVersion, contentHash, catalogHash));

        Assert.DoesNotContain(client.Errors, e => e.Code == "node-catalog-hash-mismatch");
    }
}
