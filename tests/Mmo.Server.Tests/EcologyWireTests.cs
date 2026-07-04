using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E4 (docs/ecology-v1-design.md §3/§8 E4, §5.4): headless "boot-wiring" coverage (no RunAsync, no live
// tick thread — mirrors RegionSpawnerIntegrationTests) for the WIRE PROJECTION seam: EcologyWire.BuildMessage/
// ToWireState/WorstStateOf. Drives the REAL EcologyForTests.TrySetStock seam to force known states, then asserts
// on the actual production EcologyWire methods (not a re-implementation of the mapping in the test).
public sealed class EcologyWireTests
{
    private static GameServer CreateServer()
    {
        var options = new ServerOptions(
            Port: 0,
            TickRate: 20,
            ConnectionKey: "ecology-wire-test",
            DatabaseProvider: DatabaseProvider.Sqlite,
            ConnectionString: "Data Source=:memory:",
            MigrationsPath: "unused",
            WorldWidthTiles: 64,
            WorldHeightTiles: 64,
            StepCooldownMs: 250,
            PersistenceCheckpointSeconds: 15,
            InterestRadius: 18f,
            MaxVisibleEntities: 150,
            SpawnDistribution: SpawnDistribution.Distributed,
            AdminNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        return new GameServer(options, new NullCharacterRepository());
    }

    [Theory]
    [InlineData(EcologyState.PopulationState.Depleted, EcologyPopulationState.Depleted)]
    [InlineData(EcologyState.PopulationState.Thin, EcologyPopulationState.Thin)]
    [InlineData(EcologyState.PopulationState.Healthy, EcologyPopulationState.Healthy)]
    [InlineData(EcologyState.PopulationState.Rich, EcologyPopulationState.Rich)]
    [InlineData(EcologyState.PopulationState.Overgrown, EcologyPopulationState.Overgrown)]
    public void ToWireState_MapsEveryServerStateToItsWireCounterpart(
        EcologyState.PopulationState serverState, EcologyPopulationState expectedWireState)
    {
        Assert.Equal(expectedWireState, EcologyWire.ToWireState(serverState));
    }

    [Fact]
    public void BuildMessage_CarriesTheRegionsAuthoredGeometryAndCurrentPerTypeStates()
    {
        var server = CreateServer();
        var ecology = server.EcologyForTests;
        Assert.True(ecology.Registry.TryGet("the_verge", out var theVerge));

        // Force distinct states for the two hosted types so the per-type mapping (not just the region geometry)
        // is genuinely pinned: slime -> Depleted (ratio < 0.25 of K=6), gnoll left at its seeded K (Rich; S=K
        // exactly falls in the [1.0,1.25) Rich band, not Healthy — see EcologyState.StateOf's boundaries).
        Assert.True(ecology.TrySetStock("the_verge", "slime", 0.5d));

        var message = EcologyWire.BuildMessage(ecology, theVerge);

        Assert.Equal("the_verge", message.RegionId);
        Assert.Equal("The Verge", message.DisplayName);
        Assert.Equal(theVerge.MinX, message.MinTileX);
        Assert.Equal(theVerge.MinY, message.MinTileY);
        Assert.Equal(theVerge.MaxX, message.MaxTileX);
        Assert.Equal(theVerge.MaxY, message.MaxTileY);
        Assert.Equal(2, message.Types.Count);

        RegionEcologyTypeEntry? slimeEntry = null;
        RegionEcologyTypeEntry? gnollEntry = null;
        foreach (var entry in message.Types)
        {
            if (string.Equals(entry.TypeId, "slime", StringComparison.OrdinalIgnoreCase))
            {
                slimeEntry = entry;
            }
            else if (string.Equals(entry.TypeId, "gnoll", StringComparison.OrdinalIgnoreCase))
            {
                gnollEntry = entry;
            }
        }

        Assert.NotNull(slimeEntry);
        Assert.Equal(EcologyPopulationState.Depleted, slimeEntry!.Value.State);
        Assert.NotNull(gnollEntry);
        Assert.Equal(EcologyPopulationState.Rich, gnollEntry!.Value.State);
    }

    [Fact]
    public void WorstStateOf_PicksTheMostSevereHostedType()
    {
        var server = CreateServer();
        var ecology = server.EcologyForTests;
        Assert.True(ecology.Registry.TryGet("the_verge", out var theVerge));

        // Both types start at Rich (S=K exactly). Force slime down to Depleted — the worst state must follow it,
        // even though gnoll (a "better" state) is still present in the SAME region.
        Assert.True(ecology.TrySetStock("the_verge", "slime", 0.5d));
        Assert.Equal(EcologyPopulationState.Depleted, EcologyWire.WorstStateOf(ecology, theVerge));

        // Force it into OVERGROWN territory instead — the worst state must follow that too (asymmetric severity:
        // Overgrown outranks the region's other Rich/Healthy types, mirroring EcologyLegibilityTests).
        Assert.True(ecology.TrySetStock("the_verge", "slime", 8d)); // > 1.25*K(6) = 7.5
        Assert.Equal(EcologyPopulationState.Overgrown, EcologyWire.WorstStateOf(ecology, theVerge));
    }

    // The boot-wiring test never touches persistence: GameServer's ctor only wires the repository into the
    // write-behind worker, which stays idle without sessions (mirrors RegionSpawnerIntegrationTests').
    private sealed class NullCharacterRepository : ICharacterRepository
    {
        public Task<CharacterRecord> LoadOrCreateAsync(string accountName, string displayName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Boot-wiring test: no logins expected.");

        public Task SavePositionAsync(Guid characterId, WorldVector position, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ItemStack>>([]);

        public Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
