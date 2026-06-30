using System.IO;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// MONSTER-TUNING-SAVE: coverage for the server-side WRITE seam GameServer.TrySaveMonsterTypes — the path-parameterised
// helper the admin-gated SaveMonsterTuning handler calls. Pinned against a TEMP path (never the live
// Content/monsters.json) so the test is isolated: it writes a real manifest via MonsterTypeRegistry.ToManifestJson and
// proves the file re-loads with every type's composition + tuning intact, and that an IO failure is swallowed (returns
// false + a message, never throws) so a bad disk can't crash the tick loop. The admin GATE (deny side) is covered
// end-to-end by AdminTuningIntegrationTests; the gate's allow branch is the same role check inverted, calling this
// unit-tested helper.
public sealed class GameServerMonsterSaveTests
{
    private const int TickRate = 20;

    [Fact]
    public void TrySaveMonsterTypesWritesAManifestThatReloadsIntact()
    {
        // Build from the shipped data (slime + the fully-composed gnoll glider) so the round-trip exercises the
        // selectors + non-default tint/scale/abilities — the headline "don't drop a field on Save" risk.
        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, ReadShippedManifest());

        var path = Path.Combine(Path.GetTempPath(), $"mmo-monsters-save-{Path.GetRandomFileName()}.json");
        try
        {
            Assert.True(GameServer.TrySaveMonsterTypes(registry, path, out var error));
            Assert.Equal(string.Empty, error);
            Assert.True(File.Exists(path));

            var reloaded = MonsterTypeRegistry.FromManifestJson(TickRate, File.ReadAllText(path));

            Assert.Equal(registry.Types.Count, reloaded.Types.Count);
            Assert.True(reloaded.TryGet("gnoll", out var g));
            // The gnoll's P1–P6 composition + a representative tunable must survive the disk round-trip.
            Assert.Equal("glide", g.LocomotionId);
            Assert.Equal("skirmisher", g.BehaviorId);
            Assert.Equal(new[] { "charge" }, g.AbilityIds);
            Assert.Equal(0xB5651Du, g.RenderTintRgb);
            Assert.Equal(1.4d, g.RenderScale, 6);
            Assert.Equal(200, g.MaxHealth);
            Assert.Equal(4000, g.ChargeCooldownMs);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void TrySaveMonsterTypesReturnsFalseOnAnUnwritablePathInsteadOfThrowing()
    {
        var registry = new MonsterTypeRegistry(TickRate);

        // A path whose parent directory does not exist → File.WriteAllText throws DirectoryNotFoundException, which the
        // helper must catch + report (so the tick loop never crashes on an IO error).
        var bad = Path.Combine(Path.GetTempPath(), $"mmo-no-such-dir-{Path.GetRandomFileName()}", "monsters.json");

        Assert.False(GameServer.TrySaveMonsterTypes(registry, bad, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(File.Exists(bad));
    }

    // Reads the shipped manifest from the test output dir (transitively copied via Mmo.Server.csproj), falling back to
    // the repo source path — mirrors MonsterTypeManifestTests.ReadShippedManifest.
    private static string ReadShippedManifest()
    {
        var shipped = Path.Combine(System.AppContext.BaseDirectory, "Content", "monsters.json");
        if (File.Exists(shipped))
        {
            return File.ReadAllText(shipped);
        }

        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Mmo.Server", "Content", "monsters.json");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate the shipped monsters.json.");
    }
}
