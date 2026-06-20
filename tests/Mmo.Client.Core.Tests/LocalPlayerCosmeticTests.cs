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
}
