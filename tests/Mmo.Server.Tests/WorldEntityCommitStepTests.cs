using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// S103 commit-step on release. WorldEntity.TryCommitStep finishes a near-complete step early on key-release, but
// MUST preserve the no-speedhack property: it accepts a walkable step only once the entity is at least
// acceptFraction of its cooldown into the current step, and on accept borrows the next step's FULL cooldown so the
// average step rate can never exceed the normal cadence. These tests pin: accept past the fraction (advances tile +
// StepSequence, sets next-eligible a full cooldown out), reject below the fraction, reject into a wall, and the
// cadence cap (spam commits cannot step faster than the cooldown allows).
public sealed class WorldEntityCommitStepTests
{
    private const double AcceptFraction = 0.5d;

    [Fact]
    public void CommitPastFraction_OnWalkableTile_AdvancesTileAndStepSequence()
    {
        // cooldown = 10 ticks. Step at tick 0 (accepted), then commit at tick 6 (>= 0.5*10 = 5 into the step):
        // accepted — the tile advances and StepSequence bumps once for the commit.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 10, grid)); // seq 1, lastStep=0
        Assert.Equal(1u, entity.StepSequence);

        var committed = entity.TryCommitStep(Direction8.E, 6, stepCooldownTicks: 10, AcceptFraction, grid, out var result);

        Assert.True(committed);
        Assert.True(result.Accepted);
        Assert.Equal("committed", result.Reason);
        Assert.Equal(new TileCoord(10, 8), entity.TileCoord); // advanced one tile east of (9,8)
        Assert.Equal(2u, entity.StepSequence);           // bumped once for the commit
    }

    [Fact]
    public void CommitAccept_SchedulesNextStepFromNominalEnd_NoSpeedhack()
    {
        // After an accepted EARLY commit at tick 6 (cooldown 10, step started at 0), the next step is scheduled from
        // the NOMINAL step end (tick 10), so the next eligible is tick 20 — the commit gained NO time. A normal step
        // at tick 16 (< 20) is dropped on cooldown; tick 20 moves. (This is the no-speedhack borrow: finishing early
        // on screen does not advance the schedule.)
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 10, grid));            // lastStep=0, next=10
        Assert.True(entity.TryCommitStep(Direction8.E, 6, 10, AcceptFraction, grid, out _));  // commit -> next=20 (nominal end 10 + cooldown)

        Assert.False(entity.TryStep(Direction8.E, 16, stepCooldownTicks: 10, grid, out var early));
        Assert.Equal("cooldown", early.Reason);

        Assert.True(entity.TryStep(Direction8.E, 20, stepCooldownTicks: 10, grid)); // nominal cooldown elapsed
        Assert.Equal(3u, entity.StepSequence);
    }

    [Fact]
    public void CommitBelowFraction_IsRejected_NoStateChange()
    {
        // Commit at tick 3 (< 0.5*10 = 5 into the step): rejected — the tile and StepSequence are unchanged.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 10, grid)); // lastStep=0, tile (9,8)
        var before = entity.TileCoord;
        var beforeSeq = entity.StepSequence;

        var committed = entity.TryCommitStep(Direction8.E, 3, stepCooldownTicks: 10, AcceptFraction, grid, out var result);

        Assert.False(committed);
        Assert.False(result.Accepted);
        Assert.Equal("commit_too_early", result.Reason);
        Assert.Equal(before, entity.TileCoord);
        Assert.Equal(beforeSeq, entity.StepSequence);
    }

    [Fact]
    public void CommitIntoWall_IsRejected_NoStateChange()
    {
        // A commit toward a blocked tile is rejected on the walkability gate even past the fraction.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, [new TileCoord(10, 8)]); // wall E of (9,8)

        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 10, grid)); // -> (9,8)
        var committed = entity.TryCommitStep(Direction8.E, 6, stepCooldownTicks: 10, AcceptFraction, grid, out var result);

        Assert.False(committed);
        Assert.False(result.TargetWalkable);
        Assert.Equal("blocked", result.Reason);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord); // held at the wall
    }

    [Fact]
    public void CommitDiagonalThroughCorner_IsRejected()
    {
        // S75 corner-cut rule applies to commits too: a diagonal commit whose side tile is blocked is rejected.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, [new TileCoord(10, 8)]); // after the first step we're at (9,8); block (10,8)

        Assert.True(entity.TryStep(Direction8.E, 0, stepCooldownTicks: 10, grid)); // -> (9,8)
        // Commit NE from (9,8) -> (10,7): destination open, but side tile (10,8) is blocked -> corner-cut reject.
        var committed = entity.TryCommitStep(Direction8.NE, 6, stepCooldownTicks: 10, AcceptFraction, grid, out var result);

        Assert.False(committed);
        Assert.Equal("blocked", result.Reason);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
    }

    [Fact]
    public void FirstEverCommit_OnNeverSteppedEntity_IsAccepted()
    {
        // A never-stepped entity has no last step, so its first commit is always eligible (elapsed treated as
        // infinite) — same as the first normal step being unconditionally eligible.
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(16, 16, []);

        var committed = entity.TryCommitStep(Direction8.E, 5, stepCooldownTicks: 10, AcceptFraction, grid, out var result);

        Assert.True(committed);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
        Assert.Equal(1u, entity.StepSequence);
        Assert.Equal("committed", result.Reason);
    }

    [Fact]
    public void SpammedCommits_CannotExceedAverageCadence()
    {
        // The load-bearing anti-cheat assertion: step once, then SPAM commits every tick. Because each accepted
        // commit borrows a full cooldown (nextEligible = commitTick + cooldown) and the floor rejects sub-fraction
        // commits, the tile can advance no faster than the cooldown allows on average. Over a long window the
        // accepted-move count must not exceed ceil(window / cooldown) + 1.
        const uint cooldown = 10;
        var entity = CreateEntity(tile: new TileCoord(0, 8), facing: Direction8.E);
        var grid = new TileGrid(2048, 16, []);

        var accepted = 0;
        const uint window = 500;
        for (uint tick = 0; tick <= window; tick++)
        {
            // Spam BOTH a normal step and a commit every tick (a scripted client would try both paths).
            if (entity.TryStep(Direction8.E, tick, cooldown, grid))
            {
                accepted++;
            }

            if (entity.TryCommitStep(Direction8.E, tick, cooldown, AcceptFraction, grid, out _))
            {
                accepted++;
            }
        }

        // Cadence cap: at most one move per cooldown, plus a small constant for the unconditional first move and
        // boundary rounding. ceil(500/10) = 50; allow +2 slack.
        var maxByCadence = (int)((window / cooldown) + 2);
        Assert.True(accepted <= maxByCadence, $"accepted {accepted} moves in {window} ticks at cooldown {cooldown}; cap ~{maxByCadence}");
        // And it really did move at roughly the cadence (not zero) — sanity that commits/steps fired.
        Assert.True(accepted >= (int)(window / cooldown) - 1, $"expected ~{window / cooldown} moves, got {accepted}");
    }

    // ---- NET3 authored-tick commit application (TryCommitStepAuthored) ---------------------------------
    // The loss-desync fix: a UoClientDriven commit applies at its AUTHORED tick (the predictor gate tick the client
    // banked it on), with the cooldown SCHEDULE keyed on authored time, not the receive tick. The clamp window for
    // these tests mirrors the server consts (GameServer.AuthoredTickPastWindow / AuthoredTickFutureLead).
    private const uint PastWindow = 64;
    private const uint FutureLead = 4;

    [Fact]
    public void Authored_BundledRecoveredCommit_AtOneReceiveTick_BothAccepted_AtAuthoredTicks()
    {
        // C2(authored 3) was dropped and recovered BUNDLED with C3(authored 6) at ONE receive tick (8). The OLD
        // receive-time gate rejected the second; the authored-tick path schedules each at its own authored tick a
        // cadence apart, so BOTH land — the server reaches all 3 banked steps.
        const uint cd = 3;
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(64, 64, []);

        Assert.True(entity.TryCommitStepAuthored(Direction8.E, 0, 0, cd, PastWindow, FutureLead, grid, out _)); // C1
        Assert.True(entity.TryCommitStepAuthored(Direction8.E, 3, 8, cd, PastWindow, FutureLead, grid, out _)); // C2 (bundle)
        var c3 = entity.TryCommitStepAuthored(Direction8.E, 6, 8, cd, PastWindow, FutureLead, grid, out var r3);

        Assert.True(c3);                                   // the recovered bundle's 2nd commit is ACCEPTED
        Assert.Equal("committed", r3.Reason);
        Assert.Equal(3u, entity.StepSequence);             // all 3 banked steps landed — no desync
        Assert.Equal(new TileCoord(11, 8), entity.TileCoord);
    }

    [Fact]
    public void Authored_SameTickSpamBurst_IsCappedByRealTime_NoTeleport()
    {
        // A burst of commits all authored at the same tick cannot teleport: each is paced to >= prior + cooldown,
        // and the real-time cap (serverTick + futureLead) rejects any slot beyond it. At serverTick 1 (cap 5) only
        // the slots 0 and 3 land; 6, 9, 12 are rejected and must wait for real time.
        const uint cd = 3;
        var entity = CreateEntity(tile: new TileCoord(0, 8), facing: Direction8.E);
        var grid = new TileGrid(64, 16, []);

        var accepted = 0;
        for (var i = 0; i < 5; i++)
        {
            if (entity.TryCommitStepAuthored(Direction8.E, 0, 1, cd, PastWindow, FutureLead, grid, out _))
            {
                accepted++;
            }
        }

        Assert.Equal(2, accepted);
        Assert.Equal(2u, entity.StepSequence);
    }

    [Fact]
    public void Authored_SustainedBurst_OverLongWindow_CannotExceedCadence()
    {
        // The long-run anti-speedhack guarantee: spam a commit authored at the CURRENT server tick every tick for a
        // long window. The pace (>= prior + cooldown) + real-time cap (<= serverTick + futureLead) means the rate
        // can never exceed one step per cooldown on average (plus the small futureLead head-start + boundary slack).
        const uint cd = 3;
        const uint window = 600;
        var entity = CreateEntity(tile: new TileCoord(0, 8), facing: Direction8.E);
        var grid = new TileGrid(4096, 16, []);

        var accepted = 0;
        for (uint tick = 0; tick <= window; tick++)
        {
            // A scripted client claims its step is authored RIGHT NOW (the most aggressive legal authored tick).
            if (entity.TryCommitStepAuthored(Direction8.E, tick, tick, cd, PastWindow, FutureLead, grid, out _))
            {
                accepted++;
            }
        }

        // ceil(600/3) = 200; allow the futureLead head-start + a boundary step of slack.
        var maxByCadence = (int)((window / cd) + FutureLead + 2);
        Assert.True(accepted <= maxByCadence, $"accepted {accepted} in {window} ticks at cd {cd}; cap ~{maxByCadence}");
        Assert.True(accepted >= (int)(window / cd) - 1, $"expected ~{window / cd} moves, got {accepted}");
    }

    [Fact]
    public void Authored_FarPastTick_IsClampedToWindowFloor_NotRewindingSchedule()
    {
        // A far-past authored tick (stale recovery / tamper) is clamped UP to the window floor (serverTick - past),
        // so it cannot rewind the schedule arbitrarily; it still applies once (paced).
        const uint cd = 3;
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.E);
        var grid = new TileGrid(64, 64, []);

        // serverTick 100, authored tick 0 is far below the floor (100 - 64 = 36): clamped to 36, applied.
        var ok = entity.TryCommitStepAuthored(Direction8.E, 0, 100, cd, PastWindow, FutureLead, grid, out var r);
        Assert.True(ok);
        Assert.Equal("committed", r.Reason);
        Assert.Equal(1u, entity.StepSequence);
    }

    private static WorldEntity CreateEntity(
        uint networkId = 1,
        TileCoord? tile = null,
        Direction8 facing = Direction8.S)
    {
        return new WorldEntity(
            id: networkId,
            networkId: networkId,
            EntityKind.Player,
            tile ?? TileGrid.DefaultSpawnTile,
            facing,
            $"Player{networkId}",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
