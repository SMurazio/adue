using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// N-movement-trace-live-toggle: the console MOVE-trace gate must be flippable at RUNTIME (the F3 perf-panel
// checkbox drives it), per the project's live-toggle guardrail — MMO_DEBUG_MOVEMENT is only the initial seed.
// Pins: (1) Enabled=false emits nothing but still tracks the snapshot (the F3 HUD reads it regardless);
// (2) flipping Enabled on mid-session starts emitting; (3) flipping it back off stops again.
public sealed class ClientMovementTraceToggleTests
{
    [Fact]
    public void EnabledIsLiveMutableAndGatesOnlyTheConsoleOutput()
    {
        var lines = new List<string>();
        var trace = new ClientMovementTrace(enabled: false, lines.Add);

        // Off: no console output, but the unconditional snapshot tracking still runs.
        trace.TileConfirmed(7, new TileCoord(3, 4), 11, DateTimeOffset.UtcNow, 0, 250d, new RenderPosition(3, 4));
        Assert.Empty(lines);
        Assert.Equal(7u, trace.Snapshot.LastConfirmedNetworkId);

        // Live flip ON: the same event now emits.
        trace.Enabled = true;
        trace.TileConfirmed(7, new TileCoord(4, 4), 12, DateTimeOffset.UtcNow, 0, 250d, new RenderPosition(4, 4));
        var line = Assert.Single(lines);
        Assert.Contains("event=tile_confirmed", line);

        // Live flip OFF: emission stops again (snapshot tracking continues).
        trace.Enabled = false;
        trace.TileConfirmed(7, new TileCoord(5, 4), 13, DateTimeOffset.UtcNow, 0, 250d, new RenderPosition(5, 4));
        Assert.Single(lines);
        Assert.Equal(13u, trace.Snapshot.LastConfirmedSnapshotSequence);
    }
}
