using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S89 model-B ("cosmetic lead") bar: the cosmetic driver renders the local player with the SAME present-time
// tween model A uses, but owns NO predicted tile — the confirmed tile advances ONLY on Confirm (the server ack),
// the render glides EARLY toward the held-input direction (bounded to one tile), and a DISAGREEING confirm CUTS
// the render to the confirmed tile with no reproject. These tests assert B's five invariants: logic never leads,
// the early glide is responsive + bounded, a blocked confirm cuts without a persisting overshoot, at rest the
// render is exactly the confirmed tile, and the walkability gate blocks a glide into a wall.
public sealed class LocalPlayerCosmeticTests
{
    private const double Cadence = 150d;

    // Open field: every tile walkable.
    private static bool OpenField(TileCoord _) => true;

    private static LocalPlayerCosmetic NewCosmetic(TileCoord start, Direction8 facing, Func<TileCoord, bool>? walkable = null)
        => new(start, facing, Cadence, walkable ?? OpenField);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // ---- Invariant 1: logic never leads (B banks nothing) -----------------------------------------

    [Fact]
    public void ConfirmedTile_NeverAdvancesWithoutConfirm()
    {
        // Any amount of SetIntent + Tick with NO Confirm must leave the confirmed tile untouched — B never banks
        // a tile ahead for logic. (Contrast model A, whose PredictedTile would advance on Tick.)
        var cosmetic = NewCosmetic(new TileCoord(5, 5), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        for (var t = 0; t <= 1000; t += 16)
        {
            cosmetic.Tick(Ms(t));
        }

        Assert.Equal(new TileCoord(5, 5), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void ConfirmedTile_AdvancesOnlyOnConfirm()
    {
        var cosmetic = NewCosmetic(new TileCoord(5, 5), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(50));
        Assert.Equal(new TileCoord(5, 5), cosmetic.ConfirmedTile);

        // The server confirms the step east — only now does the confirmed tile advance.
        cosmetic.Confirm(new TileCoord(6, 5), Direction8.E, Ms(150));
        Assert.Equal(new TileCoord(6, 5), cosmetic.ConfirmedTile);
    }

    // ---- Invariant 2: early glide (responsiveness), bounded by the lead cap ------------------------

    [Fact]
    public void SetIntent_GlidesRenderEarly_BeforeAnyConfirm()
    {
        // The moment input arrives, the render must move OFF the confirmed-tile center toward the held direction,
        // before any server confirm — that is the responsiveness model B exists for.
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));            // arm the lead toward (1,0)
        var early = cosmetic.Sample(Ms(Cadence / 2)); // ~half a cadence into the glide

        Assert.True(early.X > 0.0, $"render should have glided east of the confirmed center; was {early.X}");
        Assert.True(early.X < 1.0, "render should not have passed the adjacent tile (bounded by the lead cap)");
        Assert.Equal(0.0, early.Y, 6);
        // And it banked nothing.
        Assert.Equal(new TileCoord(0, 0), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void EarlyGlide_BoundedByOneTile_NeverRunsAhead()
    {
        // With NO confirm ever arriving, the glide must HOLD at the one-tile cap (paced by the confirm rate) —
        // it cannot run two, three, ... tiles ahead. Tick for a long time, then assert the render is at most one
        // tile from the confirmed center.
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        RenderPosition pos = default;
        for (var t = 0; t <= 2000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            pos = cosmetic.Sample(Ms(t));
        }

        Assert.True(pos.X <= 1.0 + 1e-9, $"render must hold at the 1-tile cap; was {pos.X}");
        Assert.True(pos.X >= 0.99, $"render should have reached the cap; was {pos.X}");
        Assert.Equal(new TileCoord(0, 0), cosmetic.ConfirmedTile);
    }

    // ---- Invariant 3: a blocked / disagreeing confirm cuts, no reproject --------------------------

    [Fact]
    public void DisagreeingConfirm_CutsToConfirmedTile_NoPersistingOvershoot()
    {
        // Glide east toward (1,0); then the server confirms we DID NOT move (blocked) — confirmed stays (0,0).
        // The render must settle onto the confirmed tile within one cadence, with NO overshoot persisting (the
        // exact symptom model A can latch on — B must converge exactly to truth).
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitStepEnabled = false; // isolate the disagreeing-confirm cut from the S103 commit-step path

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        var leadPos = cosmetic.Sample(Ms(100));
        Assert.True(leadPos.X > 0.0, "precondition: glided east");

        // Disagreeing confirm: the server did not advance us. Stop moving (key released) so nothing re-leads.
        cosmetic.SetIntent(false, Direction8.E, Ms(100));
        cosmetic.Confirm(new TileCoord(0, 0), Direction8.E, Ms(100));

        // Within one cadence the render is back on the confirmed tile exactly; no overshoot lingers.
        var settled = cosmetic.Sample(Ms(100 + Cadence + 1));
        Assert.Equal(0.0, settled.X, 6);
        Assert.Equal(0.0, settled.Y, 6);
        Assert.Equal(new TileCoord(0, 0), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void AgreeingConfirm_FlowsSeamlessly_ConfirmedAdvances()
    {
        // When the server AGREES with the lead (confirms the very tile we were gliding toward), the confirmed
        // tile advances and the render flows on continuously — no cut back.
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Confirm(new TileCoord(1, 0), Direction8.E, Ms(150)); // server agreed: stepped east

        Assert.Equal(new TileCoord(1, 0), cosmetic.ConfirmedTile);
        // Continuing to hold east keeps gliding toward (2,0) — no snap back to (1,0).
        cosmetic.Tick(Ms(160));
        var glide = cosmetic.Sample(Ms(150 + Cadence / 2));
        Assert.True(glide.X > 1.0, $"render should glide on past the confirmed tile toward (2,0); was {glide.X}");
        Assert.True(glide.X <= 2.0 + 1e-9, "still bounded by one tile ahead of the new confirmed tile");
    }

    // ---- S91: release snaps instantly to the confirmed tile (no backward drift) -------------------

    [Fact]
    public void Release_SnapsInstantlyToConfirmedTile_NoBackwardDrift()
    {
        // Glide east off the confirmed-tile center, then release. The render must be EXACTLY the confirmed-tile
        // center IMMEDIATELY at release time — not a cadence later. (Before S91 the release tweened back over a
        // full cadence, so Sample(now) would still be mid-glide east of center; after S91 it snaps.)
        var cosmetic = NewCosmetic(new TileCoord(4, 4), Direction8.E);
        cosmetic.CommitStepEnabled = false; // isolate the S91 snap from the S103 commit-step path

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        var lead = cosmetic.Sample(Ms(100));
        Assert.True(lead.X > 4.0, $"precondition: render glided east of the confirmed center; was {lead.X}");

        // Release at t=100. The confirmed tile is still (4,4); the render must snap to it at the SAME instant.
        cosmetic.SetIntent(false, Direction8.E, Ms(100));

        var atRelease = cosmetic.Sample(Ms(100));
        Assert.Equal(4.0, atRelease.X, 6);
        Assert.Equal(4.0, atRelease.Y, 6);
        Assert.Equal(new TileCoord(4, 4), cosmetic.ConfirmedTile);
    }

    // ---- S102: SnapOnRelease == false soft-settles instead of hard-snapping -----------------------

    [Fact]
    public void Release_WithSnapOnReleaseOff_DoesNotHardSnap_ButSettlesOverCadence()
    {
        // With SnapOnRelease OFF (model B), releasing must NOT cut the render to the confirmed tile at the same
        // instant (the S91 hard snap). Instead the lead glide unwinds onto the confirmed center over one cadence —
        // so immediately at release the render is still east of center, and a cadence later it has settled exactly.
        var cosmetic = NewCosmetic(new TileCoord(4, 4), Direction8.E);
        cosmetic.SnapOnRelease = false;
        cosmetic.CommitStepEnabled = false; // isolate the S102 soft-settle from the S103 commit-step path

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        var lead = cosmetic.Sample(Ms(100));
        Assert.True(lead.X > 4.0, $"precondition: render glided east of the confirmed center; was {lead.X}");

        // Release at t=100. SnapOnRelease is off, so the render must NOT be on the confirmed center yet — the glide
        // settles over a cadence rather than snapping.
        cosmetic.SetIntent(false, Direction8.E, Ms(100));
        var atRelease = cosmetic.Sample(Ms(100));
        Assert.True(atRelease.X > 4.0, $"SnapOnRelease off: render should still be east of center at release; was {atRelease.X}");

        // One cadence later it has settled exactly onto the confirmed tile (the destination is truth, no overshoot).
        var settled = cosmetic.Sample(Ms(100 + Cadence + 1));
        Assert.Equal(4.0, settled.X, 6);
        Assert.Equal(4.0, settled.Y, 6);
        Assert.Equal(new TileCoord(4, 4), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void Release_WithSnapOnReleaseOn_StillHardSnaps()
    {
        // The default (SnapOnRelease == true) must keep the S91 hard snap byte-for-byte: render is EXACTLY the
        // confirmed center at the release instant. Guards the new flag's default against a regression.
        var cosmetic = NewCosmetic(new TileCoord(4, 4), Direction8.E);
        cosmetic.CommitStepEnabled = false; // isolate the S91 hard snap from the S103 commit-step path
        Assert.True(cosmetic.SnapOnRelease); // default

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Sample(Ms(100));

        cosmetic.SetIntent(false, Direction8.E, Ms(100));
        var atRelease = cosmetic.Sample(Ms(100));
        Assert.Equal(4.0, atRelease.X, 6);
        Assert.Equal(4.0, atRelease.Y, 6);
    }

    // ---- Invariant 4: at rest == confirmed exactly (no latch) -------------------------------------

    [Fact]
    public void AtRest_RenderSettlesExactlyOnConfirmedTile()
    {
        var cosmetic = NewCosmetic(new TileCoord(3, 3), Direction8.E);

        // Move a bit, then release; with a steady confirmed tile the render must converge EXACTLY onto it.
        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Sample(Ms(80));

        cosmetic.SetIntent(false, Direction8.E, Ms(80));
        cosmetic.Confirm(new TileCoord(3, 3), Direction8.E, Ms(80)); // server: still on (3,3)

        var settled = cosmetic.Sample(Ms(80 + Cadence + 50));
        Assert.Equal(3.0, settled.X, 6);
        Assert.Equal(3.0, settled.Y, 6);
        Assert.Equal(new TileCoord(3, 3), cosmetic.ConfirmedTile);
    }

    // ---- Invariant 5: walkability gate — no early glide into a blocked tile -----------------------

    [Fact]
    public void WalkabilityGate_DoesNotGlideIntoBlockedTile()
    {
        // The tile east of (0,0) — i.e. (1,0) — is blocked. Pressing east must NOT start an early glide into it;
        // the render stays on the confirmed tile (no glide-into-wall-then-snap).
        bool Walkable(TileCoord t) => t != new TileCoord(1, 0);
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E, Walkable);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        for (var t = 0; t <= 300; t += 16)
        {
            cosmetic.Tick(Ms(t));
        }

        var pos = cosmetic.Sample(Ms(300));
        Assert.Equal(0.0, pos.X, 6);
        Assert.Equal(0.0, pos.Y, 6);
        Assert.Equal(new TileCoord(0, 0), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void WalkabilityGate_BlocksDiagonalCornerCut()
    {
        // S75 corner-cut rule mirrored: a diagonal glide (NE from (0,0) -> (1,1)) is gated even when the
        // destination is walkable if either orthogonal cut tile ((1,0) or (0,1)) is blocked.
        bool Walkable(TileCoord t) => t != new TileCoord(1, 0); // block one cut tile
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.NE, Walkable);

        cosmetic.SetIntent(true, Direction8.NE, Ms(0));
        for (var t = 0; t <= 300; t += 16)
        {
            cosmetic.Tick(Ms(t));
        }

        var pos = cosmetic.Sample(Ms(300));
        Assert.Equal(0.0, pos.X, 6);
        Assert.Equal(0.0, pos.Y, 6);
    }

    // ---- S92: "accept/deny only" (LeadEnabled = false) — the SAME driver with the forward lead OFF ------------
    //
    // With LeadEnabled = false the avatar must move ONLY on a confirmed step: no early glide (Tick never leads),
    // a deny (unchanged-tile Confirm) leaves it put, and release never snaps (there is no lead to unwind). This is
    // the mode the F5 "Accept/deny only (no lead)" checkbox selects; default B (LeadEnabled = true) is unchanged
    // above. By construction it cannot produce the lead overshoot / release snap that the camera could pop on.

    private static LocalPlayerCosmetic NewAcceptDeny(TileCoord start, Direction8 facing, Func<TileCoord, bool>? walkable = null)
    {
        var cosmetic = NewCosmetic(start, facing, walkable);
        cosmetic.LeadEnabled = false;
        return cosmetic;
    }

    [Fact]
    public void AcceptDeny_NoPreMovement_RenderStaysExactlyOnConfirmedTile()
    {
        // SetIntent(moving) + lots of Tick with NO Confirm must leave the render EXACTLY on the confirmed-tile
        // center — it never glides ahead (contrast the lead-enabled SetIntent_GlidesRenderEarly test).
        var cosmetic = NewAcceptDeny(new TileCoord(5, 5), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        RenderPosition pos = default;
        for (var t = 0; t <= 1000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            pos = cosmetic.Sample(Ms(t));
        }

        Assert.Equal(5.0, pos.X, 6);
        Assert.Equal(5.0, pos.Y, 6);
        Assert.Equal(new TileCoord(5, 5), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void AcceptDeny_MovesOnlyOnAccept_TweensTileToTileOverOneCadence()
    {
        // After a Confirm advances the tile, the render must tween smoothly tile-to-tile and REACH the new
        // confirmed center over one cadence — a smooth step, not a teleport.
        var cosmetic = NewAcceptDeny(new TileCoord(0, 0), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0)); // no lead armed (LeadEnabled = false)
        Assert.Equal(0.0, cosmetic.Sample(Ms(0)).X, 6); // still exactly on (0,0)

        // Server accepts the step east at t=0; the render tweens toward (1,0) over one cadence.
        cosmetic.Confirm(new TileCoord(1, 0), Direction8.E, Ms(0));
        Assert.Equal(new TileCoord(1, 0), cosmetic.ConfirmedTile);

        // Mid-tween: between the two centers, not teleported.
        var mid = cosmetic.Sample(Ms(Cadence / 2));
        Assert.True(mid.X > 0.0 && mid.X < 1.0, $"render should be mid-step between (0,0) and (1,0); was {mid.X}");

        // After one cadence it has reached the new confirmed center.
        var done = cosmetic.Sample(Ms(Cadence + 1));
        Assert.Equal(1.0, done.X, 6);
        Assert.Equal(0.0, done.Y, 6);
    }

    [Fact]
    public void AcceptDeny_DenyLeavesRenderWhereItIs()
    {
        // A deny = an unchanged-tile Confirm. With no lead, the render is already on the confirmed tile, so a
        // deny must leave it exactly there (no move).
        var cosmetic = NewAcceptDeny(new TileCoord(2, 2), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        for (var t = 0; t <= 200; t += 16)
        {
            cosmetic.Tick(Ms(t));
        }

        Assert.Equal(2.0, cosmetic.Sample(Ms(200)).X, 6); // never left the tile

        // Deny: server confirms the tile is unchanged.
        cosmetic.Confirm(new TileCoord(2, 2), Direction8.E, Ms(200));
        var after = cosmetic.Sample(Ms(200 + Cadence + 1));
        Assert.Equal(2.0, after.X, 6);
        Assert.Equal(2.0, after.Y, 6);
        Assert.Equal(new TileCoord(2, 2), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void AcceptDeny_ReleaseDoesNotSnap_InFlightConfirmTweenContinues()
    {
        // Releasing mid-Confirm-tween must NOT jump the render: there is no lead overshoot to snap away, and the
        // in-flight tween is toward a confirmed (truth) tile, so it continues to completion. Assert no
        // instantaneous position jump at the release instant.
        var cosmetic = NewAcceptDeny(new TileCoord(0, 0), Direction8.E);

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Confirm(new TileCoord(1, 0), Direction8.E, Ms(0)); // accepted step; tween (0,0)->(1,0) over a cadence

        // Mid-tween position just BEFORE release.
        var before = cosmetic.Sample(Ms(Cadence / 2));
        Assert.True(before.X > 0.0 && before.X < 1.0, $"precondition: mid-step; was {before.X}");

        // Release at the SAME instant: the render must not jump (no snap to either center).
        cosmetic.SetIntent(false, Direction8.E, Ms(Cadence / 2));
        var atRelease = cosmetic.Sample(Ms(Cadence / 2));
        Assert.Equal(before.X, atRelease.X, 6); // no instantaneous jump
        Assert.Equal(before.Y, atRelease.Y, 6);

        // And the in-flight tween continues to the confirmed tile (it does not stall or reverse).
        var done = cosmetic.Sample(Ms(Cadence + 1));
        Assert.Equal(1.0, done.X, 6);
        Assert.Equal(0.0, done.Y, 6);
    }

    [Fact]
    public void AcceptDeny_NeverArmsLeadTarget_ConfirmedNeverAdvancesWithoutConfirm()
    {
        // Belt-and-braces: across a long hold the confirmed tile must never advance without a Confirm (B banks
        // nothing either, but here we also assert the render itself stays put — the accept/deny invariant).
        var cosmetic = NewAcceptDeny(new TileCoord(7, 7), Direction8.N);

        cosmetic.SetIntent(true, Direction8.N, Ms(0));
        for (var t = 0; t <= 2000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            var pos = cosmetic.Sample(Ms(t));
            Assert.Equal(7.0, pos.X, 6);
            Assert.Equal(7.0, pos.Y, 6);
        }

        Assert.Equal(new TileCoord(7, 7), cosmetic.ConfirmedTile);
    }

    // ---- S94: live-tunable cosmetic lead distance (MaxLeadTiles) ----------------------------------
    //
    // MaxLeadTiles bounds how far model B's render glides ahead of the confirmed tile before holding. Default 1.0
    // = current model B (the EarlyGlide_BoundedByOneTile invariant above pins it). A lower value shortens the
    // visible lead: 0.5 holds the held render at half a tile; 0.0 keeps the render on the confirmed center while
    // moving (no visible lead — like accept/deny, but via the bound, with LeadEnabled still true). The setter
    // clamps to [0, 1].

    [Fact]
    public void MaxLeadTiles_Half_HeldRenderSettlesAtHalfTile()
    {
        // Glide east with NO confirm: with MaxLeadTiles = 0.5 the held render must settle at ~0.5 tile from the
        // confirmed center (the lever bounds the lead) — not ~1.0 as with the default.
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.MaxLeadTiles = 0.5d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        RenderPosition pos = default;
        for (var t = 0; t <= 2000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            pos = cosmetic.Sample(Ms(t));
        }

        Assert.Equal(0.5d, pos.X, 6);
        Assert.Equal(0.0d, pos.Y, 6);
        Assert.Equal(new TileCoord(0, 0), cosmetic.ConfirmedTile);
    }

    [Fact]
    public void MaxLeadTiles_Half_NeverExceedsHalfTileWhileGliding()
    {
        // Across the whole glide the render must never exceed the 0.5-tile bound (not just at the held endpoint).
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.MaxLeadTiles = 0.5d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        for (var t = 0; t <= 2000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            var pos = cosmetic.Sample(Ms(t));
            Assert.True(pos.X <= 0.5d + 1e-9, $"render must stay within the 0.5-tile lead bound; was {pos.X}");
        }
    }

    [Fact]
    public void MaxLeadTiles_Zero_NoVisibleLead_RenderStaysOnConfirmedCenter()
    {
        // MaxLeadTiles = 0.0 => the render holds on the confirmed-tile center while moving (no visible lead), even
        // though LeadEnabled is true (the bound, not LeadEnabled, suppresses the visible glide).
        var cosmetic = NewCosmetic(new TileCoord(3, 3), Direction8.E);
        cosmetic.MaxLeadTiles = 0.0d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        for (var t = 0; t <= 1000; t += 16)
        {
            cosmetic.Tick(Ms(t));
            var pos = cosmetic.Sample(Ms(t));
            Assert.Equal(3.0d, pos.X, 6);
            Assert.Equal(3.0d, pos.Y, 6);
        }

        Assert.Equal(new TileCoord(3, 3), cosmetic.ConfirmedTile);
    }

    // ---- S103: commit-step on release ------------------------------------------------------------------
    //
    // When the cosmetic lead has glided PAST CommitThreshold onto the next (walkable) tile at release, SetIntent
    // must NOT snap back: it returns ShouldCommit (so MmoClient sends a server-validated commit) and keeps the
    // render tweening to that tile. Accept (a Confirm to the committed tile) flows seamlessly; reject (SnapTo the
    // confirmed tile) cuts back. Below the threshold the existing S102 behaviour applies (no commit).

    [Fact]
    public void ReleasePastThreshold_DoesNotSnap_ReturnsCommitDecision_AndKeepsGliding()
    {
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitThreshold = 0.7d;

        // Glide most of the way onto (1,0): sample near the end of the cadence so progress >= 0.7.
        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        var lead = cosmetic.Sample(Ms(Cadence * 0.9)); // ~0.9 of the way -> past 0.7
        Assert.True(lead.X >= 0.7, $"precondition: render glided past threshold; was {lead.X}");

        // Release: past threshold -> commit decision, NO snap.
        var decision = cosmetic.SetIntent(false, Direction8.E, Ms(Cadence * 0.9));
        Assert.True(decision.ShouldCommit);
        Assert.Equal(new TileCoord(1, 0), decision.CommitTarget);
        Assert.Equal(Direction8.E, decision.Direction);
        Assert.True(cosmetic.HasPendingCommit);

        // The render did NOT snap back to (0,0): it is still east of center and finishing the glide to (1,0).
        var atRelease = cosmetic.Sample(Ms(Cadence * 0.9));
        Assert.True(atRelease.X >= 0.7, $"render must keep gliding to the committed tile, not snap back; was {atRelease.X}");

        // It reaches the committed tile shortly after (the in-flight tween continues to (1,0)).
        var done = cosmetic.Sample(Ms(Cadence + 1));
        Assert.Equal(1.0, done.X, 6);
    }

    [Fact]
    public void CommitAccept_ConfirmToTarget_ClearsPending_RenderStays()
    {
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitThreshold = 0.7d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Sample(Ms(Cadence * 0.9));
        var decision = cosmetic.SetIntent(false, Direction8.E, Ms(Cadence * 0.9));
        Assert.True(decision.ShouldCommit);

        // Server ACCEPTS: confirms the committed tile (1,0). Pending clears and the render flows onto it.
        cosmetic.Confirm(new TileCoord(1, 0), Direction8.E, Ms(Cadence));
        Assert.False(cosmetic.HasPendingCommit);
        Assert.Equal(new TileCoord(1, 0), cosmetic.ConfirmedTile);

        var settled = cosmetic.Sample(Ms(Cadence * 2 + 1));
        Assert.Equal(1.0, settled.X, 6);
        Assert.Equal(0.0, settled.Y, 6);
    }

    [Fact]
    public void CommitReject_UnchangedConfirm_DoesNotPrematurelyCutBack()
    {
        // The highest-risk timing: a confirm at the OLD (unchanged) tile while the commit is pending must NOT cut the
        // render back — that would misread a not-yet-arrived accept as a reject. The render keeps gliding to the
        // committed tile, and the commit stays pending (MmoClient's grace owns the eventual reject snap).
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitThreshold = 0.7d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Sample(Ms(Cadence * 0.9));
        cosmetic.SetIntent(false, Direction8.E, Ms(Cadence * 0.9));

        // Unchanged confirm at (0,0) (commit not yet processed): pending stays, render keeps heading to (1,0).
        cosmetic.Confirm(new TileCoord(0, 0), Direction8.E, Ms(Cadence * 0.95));
        Assert.True(cosmetic.HasPendingCommit);
        var stillGliding = cosmetic.Sample(Ms(Cadence + 1));
        Assert.Equal(1.0, stillGliding.X, 6); // reached the committed tile, NOT snapped back to (0,0)

        // Now the explicit reject: MmoClient snaps the render back to the confirmed tile.
        cosmetic.SnapTo(new TileCoord(0, 0), Ms(Cadence + 1));
        Assert.False(cosmetic.HasPendingCommit);
        var snapped = cosmetic.Sample(Ms(Cadence + 1));
        Assert.Equal(0.0, snapped.X, 6);
        Assert.Equal(0.0, snapped.Y, 6);
    }

    [Fact]
    public void ReleaseBelowThreshold_DoesNotCommit_TakesS102ReleaseBranch()
    {
        // Below the threshold the commit is NOT triggered — the S102 release applies (here SnapOnRelease default
        // true -> hard snap to confirmed). With a HIGH threshold (0.95) an early release (small progress) must not
        // commit.
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitThreshold = 0.95d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        var lead = cosmetic.Sample(Ms(Cadence * 0.3)); // only ~0.3 onto the next tile
        Assert.True(lead.X > 0.0 && lead.X < 0.95, "precondition: below the threshold");

        var decision = cosmetic.SetIntent(false, Direction8.E, Ms(Cadence * 0.3));
        Assert.False(decision.ShouldCommit);
        Assert.False(cosmetic.HasPendingCommit);
        // S102 default (SnapOnRelease true): hard snap to the confirmed tile.
        var atRelease = cosmetic.Sample(Ms(Cadence * 0.3));
        Assert.Equal(0.0, atRelease.X, 6);
    }

    [Fact]
    public void CommitDisabled_NeverCommits_EvenPastThreshold()
    {
        // With CommitStepEnabled off, a release past the threshold takes the normal S102 path (no commit).
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        cosmetic.CommitStepEnabled = false;
        cosmetic.CommitThreshold = 0.7d;

        cosmetic.SetIntent(true, Direction8.E, Ms(0));
        cosmetic.Tick(Ms(0));
        cosmetic.Sample(Ms(Cadence * 0.9));

        var decision = cosmetic.SetIntent(false, Direction8.E, Ms(Cadence * 0.9));
        Assert.False(decision.ShouldCommit);
        Assert.False(cosmetic.HasPendingCommit);
    }

    [Fact]
    public void CommitThreshold_DefaultIsPointFiveFive_AndSetterClampsToUnitRange()
    {
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        Assert.Equal(0.55d, cosmetic.CommitThreshold, 6);

        cosmetic.CommitThreshold = 5.0d;
        Assert.Equal(1.0d, cosmetic.CommitThreshold, 6);

        cosmetic.CommitThreshold = -1.0d;
        Assert.Equal(0.0d, cosmetic.CommitThreshold, 6);

        cosmetic.CommitThreshold = 0.5d;
        Assert.Equal(0.5d, cosmetic.CommitThreshold, 6);
    }

    [Fact]
    public void MaxLeadTiles_DefaultIsOne_AndSetterClampsToUnitRange()
    {
        // Default is 1.0 (= current model B). The setter clamps out-of-range inputs to [0, 1].
        var cosmetic = NewCosmetic(new TileCoord(0, 0), Direction8.E);
        Assert.Equal(1.0d, cosmetic.MaxLeadTiles, 6);

        cosmetic.MaxLeadTiles = 5.0d;
        Assert.Equal(1.0d, cosmetic.MaxLeadTiles, 6);

        cosmetic.MaxLeadTiles = -2.0d;
        Assert.Equal(0.0d, cosmetic.MaxLeadTiles, 6);

        cosmetic.MaxLeadTiles = 0.25d;
        Assert.Equal(0.25d, cosmetic.MaxLeadTiles, 6);
    }
}
