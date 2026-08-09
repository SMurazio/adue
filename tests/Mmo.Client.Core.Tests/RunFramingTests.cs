using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// ADUE P2-C (todo/N-p2-title-and-floor-frame.md): pins the PURE run-framing copy decisions the Godot run panel renders
// verbatim — the active-run floor label, and the end-screen headline + "how far you got" beat for every RunOutcome
// (clear / wipe incl. the close-call variant / abandoned / none). This is the part the orchestrator can verify; the
// on-screen look, placement, and the copy's FEEL are a HUMAN feel-test, not covered here.
public sealed class RunFramingTests
{
    // ---- the active-run floor label ------------------------------------------------------------------------

    [Fact]
    public void FloorLabel_NamesFloorOneAndTheSunderer()
    {
        Assert.Equal("FLOOR 1 · The Sunderer", RunFraming.FloorLabel);
        Assert.Contains($"FLOOR {RunFraming.CurrentFloor}", RunFraming.FloorLabel);
        Assert.Contains(RunFraming.BossName, RunFraming.FloorLabel);
    }

    // ---- the end-screen lines, per outcome -----------------------------------------------------------------

    [Fact]
    public void Compose_Clear_HeadlinesClearedAndFloorClearedBeat()
    {
        var lines = RunFraming.Compose(RunOutcome.Clear, bossHealthPercent: 0);

        Assert.Equal(RunFraming.ClearHeadline, lines.Headline);
        Assert.Equal("FLOOR 1 CLEARED", lines.FloorBeat);
        Assert.Contains("CLEARED", lines.FloorBeat);
    }

    [Fact]
    public void Compose_Wipe_HeadlinesWipedAndBossStillStands()
    {
        // A wipe with the boss still healthy: the flat "still stands" beat, not the near-miss line.
        var lines = RunFraming.Compose(RunOutcome.Wipe, bossHealthPercent: 80);

        Assert.Equal(RunFraming.WipeHeadline, lines.Headline);
        Assert.Equal(RunFraming.StandsBeat, lines.FloorBeat);
        Assert.NotEqual(RunFraming.CloseCallBeat, lines.FloorBeat);
    }

    [Fact]
    public void Compose_Wipe_CloseCall_UsesTheNearMissBeat()
    {
        // Boss left at or under the close-call threshold (but not dead): the "so close" wipe line.
        var lines = RunFraming.Compose(RunOutcome.Wipe, bossHealthPercent: RunFraming.CloseCallPercent);

        Assert.Equal(RunFraming.WipeHeadline, lines.Headline);
        Assert.Equal(RunFraming.CloseCallBeat, lines.FloorBeat);
    }

    [Theory]
    [InlineData(0)]   // boss already dead on a "wipe" (shouldn't happen) → NOT a close-call; flat beat.
    [InlineData(21)]  // just above the threshold → flat beat.
    [InlineData(100)]
    public void Compose_Wipe_OutsideCloseCallBand_UsesFlatBeat(int pct)
    {
        var lines = RunFraming.Compose(RunOutcome.Wipe, (byte)pct);
        Assert.Equal(RunFraming.StandsBeat, lines.FloorBeat);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(RunFraming.CloseCallPercent)]
    public void Compose_Wipe_InsideCloseCallBand_UsesNearMissBeat(int pct)
    {
        var lines = RunFraming.Compose(RunOutcome.Wipe, (byte)pct);
        Assert.Equal(RunFraming.CloseCallBeat, lines.FloorBeat);
    }

    [Theory]
    [InlineData(RunOutcome.Abandoned)]
    [InlineData(RunOutcome.None)]
    public void Compose_AbandonedOrNone_HeadlinesEndedWithBossStanding(RunOutcome outcome)
    {
        // These never actually reach a client end screen, but Compose is total: a graceful, deterministic fallback.
        var lines = RunFraming.Compose(outcome, bossHealthPercent: 50);

        Assert.Equal(RunFraming.EndedHeadline, lines.Headline);
        Assert.Equal(RunFraming.StandsBeat, lines.FloorBeat);
    }

    [Fact]
    public void Compose_EveryOutcome_YieldsNonEmptyLines()
    {
        foreach (var outcome in new[] { RunOutcome.None, RunOutcome.Clear, RunOutcome.Wipe, RunOutcome.Abandoned })
        {
            var lines = RunFraming.Compose(outcome, bossHealthPercent: 42);
            Assert.False(string.IsNullOrWhiteSpace(lines.Headline));
            Assert.False(string.IsNullOrWhiteSpace(lines.FloorBeat));
        }
    }
}
