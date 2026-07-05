using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the client half of the deadline form — the pure fill
// arithmetic, and MmoClient's telegraph projection (TelegraphMessage in → TelegraphDecalState list out) driven
// headlessly with a stamped poll clock (SetCurrentTimeForTests) exactly like a real poll would stamp arrivals.
// The load-bearing acceptance pin: the fill hits EXACTLY 1.0 at the estimated resolve tick T, never early off a
// receive-time restart — that is the whole latency-compensation trick.
public sealed class TelegraphDecalTests
{
    private const int TickRate = 20;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // ---- the pure fill math ----

    [Fact]
    public void FillProgressHitsOneExactlyAtT()
    {
        // start 1000, resolve 1030 (a 1.5 s windup @ 20 Hz).
        Assert.Equal(0d, TelegraphFill.Progress(1000d, 1000, 1030), 9);
        Assert.Equal(0.5d, TelegraphFill.Progress(1015d, 1000, 1030), 9);
        Assert.Equal(1d, TelegraphFill.Progress(1030d, 1000, 1030), 9);

        // Clamped both sides: an estimate slightly behind start (snapshot-starved clock) renders empty, not
        // negative; past T it saturates at full (the flash window), never > 1.
        Assert.Equal(0d, TelegraphFill.Progress(998.7d, 1000, 1030), 9);
        Assert.Equal(1d, TelegraphFill.Progress(1042d, 1000, 1030), 9);
    }

    [Fact]
    public void FillProgressDegenerateWindowIsFull()
    {
        // resolve <= start never leaves the server codepaths (windup >= 1 tick), but a hostile packet could carry
        // it — "resolves no later than its own start" reads as already full, and can never divide by zero.
        Assert.Equal(1d, TelegraphFill.Progress(5d, 10, 10), 9);
        Assert.Equal(1d, TelegraphFill.Progress(5d, 10, 3), 9);
    }

    // ---- MmoClient's projection ----

    // One shared setup: hello (tick rate) + a snapshot at a stamped local time so the cosmetic clock snaps to a
    // known (localMs → serverTick) anchor: serverTick 1000 at local 5000 ms.
    private static MmoClient CreateAnchoredClient()
    {
        var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));
        client.OutboundSinkForTests = (_, _) => { };
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, TickRate, 140, 30, 0.5f));
        client.SetCurrentTimeForTests(Ms(5000));
        client.HandleMessageForTests(new WorldSnapshotMessage(1000u, 1u, []));
        return client;
    }

    private static TelegraphMessage Slam(ulong id, uint startTick, uint resolveTick) => new(
        id,
        new TelegraphShape(TelegraphShapeKind.Circle, new WorldVector(10d, 12d), 2.5d),
        startTick,
        resolveTick);

    [Fact]
    public void DecalFillsOnTheSharedDeadlineAndFlashesAtT()
    {
        using var client = CreateAnchoredClient();
        var decals = new List<TelegraphDecalState>();

        // Cast at the anchor tick: start 1000, resolve 1030 (1.5 s). Clock: est(t) = 1000 + (t − 5000 ms)/50.
        client.HandleMessageForTests(Slam(7, 1000, 1030));

        client.CopyTelegraphDecalsTo(decals, Ms(5000));
        var decal = Assert.Single(decals);
        Assert.Equal(7UL, decal.TelegraphId);
        Assert.Equal(TelegraphShapeKind.Circle, decal.Kind);
        Assert.Equal(new WorldVector(10d, 12d), decal.Origin);
        Assert.Equal(2.5d, decal.Radius, 9); // HONEST TELEGRAPH: exactly the wire radius, no bias
        Assert.Equal(0d, decal.Progress, 9);
        Assert.False(decal.Resolved);

        // Mid-windup: 750 ms in → estimated tick 1015 → half full, not yet resolved.
        client.CopyTelegraphDecalsTo(decals, Ms(5750));
        decal = Assert.Single(decals);
        Assert.Equal(0.5d, decal.Progress, 9);
        Assert.False(decal.Resolved);

        // AT estimated T (1500 ms in → tick 1030): the fill hits EXACTLY 1.0 and the resolve flash begins.
        client.CopyTelegraphDecalsTo(decals, Ms(6500));
        decal = Assert.Single(decals);
        Assert.Equal(1d, decal.Progress, 9);
        Assert.True(decal.Resolved);

        // Inside the flash window (0.35 s @ 20 Hz = 7 ticks past T) the decal persists, saturated.
        client.CopyTelegraphDecalsTo(decals, Ms(6500 + 200));
        decal = Assert.Single(decals);
        Assert.Equal(1d, decal.Progress, 9);
        Assert.True(decal.Resolved);

        // Past the flash window it self-prunes — no despawn message exists; gone stays gone.
        client.CopyTelegraphDecalsTo(decals, Ms(6500 + 400));
        Assert.Empty(decals);
        client.CopyTelegraphDecalsTo(decals, Ms(6500 + 401));
        Assert.Empty(decals);
    }

    // The late-join payoff of the deadline form: a viewer that receives the telegraph mid-windup (AOI-enter) gets
    // the SAME startTick/resolveTick and therefore renders the correct REMAINING fill — never a fill restarted at
    // its own receive time (which would complete after the hit already landed).
    [Fact]
    public void LateJoinRendersRemainingFillNotARestart()
    {
        using var client = CreateAnchoredClient();
        var decals = new List<TelegraphDecalState>();

        // The telegraph arrives 900 ms after its start tick (est now = 1018 of a 1000→1030 window).
        client.SetCurrentTimeForTests(Ms(5900));
        client.HandleMessageForTests(Slam(9, 1000, 1030));

        client.CopyTelegraphDecalsTo(decals, Ms(5900));
        var decal = Assert.Single(decals);
        Assert.Equal(0.6d, decal.Progress, 9); // (1018 − 1000) / 30 — 60% already elapsed
        Assert.False(decal.Resolved);

        // And it still completes at the SHARED T, i.e. only 600 ms after arrival.
        client.CopyTelegraphDecalsTo(decals, Ms(6500));
        decal = Assert.Single(decals);
        Assert.Equal(1d, decal.Progress, 9);
        Assert.True(decal.Resolved);
    }

    // A duplicate announcement (same id) upserts — one decal, not two; and distinct ids render side by side.
    [Fact]
    public void DuplicateIdUpsertsAndDistinctIdsCoexist()
    {
        using var client = CreateAnchoredClient();
        var decals = new List<TelegraphDecalState>();

        client.HandleMessageForTests(Slam(1, 1000, 1030));
        client.HandleMessageForTests(Slam(1, 1000, 1030));
        client.HandleMessageForTests(Slam(2, 1010, 1040));

        client.CopyTelegraphDecalsTo(decals, Ms(5000));
        Assert.Equal(2, decals.Count);
        Assert.Contains(decals, d => d.TelegraphId == 1);
        Assert.Contains(decals, d => d.TelegraphId == 2);
    }

    // TELEGRAPH SHAPES WEDGE+LINE: the projection carries EVERY shape param through to the decal state, so the Godot
    // pass can draw the exact drawn=hit shape (wedge/line) from the wire fields alone. A wedge ships its aim + half-angle;
    // a line ships its aim + half-width (with Radius carrying the length). Circle leaves the extra params 0 (see above).
    [Fact]
    public void ProjectionCarriesWedgeAndLineShapeParams()
    {
        using var client = CreateAnchoredClient();
        var decals = new List<TelegraphDecalState>();

        client.HandleMessageForTests(new TelegraphMessage(
            10, TelegraphShape.Wedge(new WorldVector(4d, 5d), 2.75d, aimRadians: 1.0d, halfAngleRadians: 0.5d), 1000, 1030));
        client.HandleMessageForTests(new TelegraphMessage(
            11, TelegraphShape.Line(new WorldVector(-2d, 3d), length: 8d, aimRadians: 2.0d, halfWidth: 1d), 1000, 1030));

        client.CopyTelegraphDecalsTo(decals, Ms(5000));

        var wedge = Assert.Single(decals, d => d.TelegraphId == 10);
        Assert.Equal(TelegraphShapeKind.Wedge, wedge.Kind);
        Assert.Equal(2.75d, wedge.Radius, 6);
        Assert.Equal(1.0d, wedge.AimRadians, 3);
        Assert.Equal(0.5d, wedge.HalfAngleRadians, 3);

        var line = Assert.Single(decals, d => d.TelegraphId == 11);
        Assert.Equal(TelegraphShapeKind.Line, line.Kind);
        Assert.Equal(8d, line.Radius, 6);           // Radius carries the line length
        Assert.Equal(2.0d, line.AimRadians, 3);
        Assert.Equal(1d, line.HalfWidth, 6);
    }

    // Before any snapshot lands there is no clock estimate — a telegraph renders at progress 0 (empty ring, no
    // guess, no prune) instead of throwing or filling blind. Contrived ordering (the login snapshot precedes any
    // cast in practice) but the projection must not depend on it.
    [Fact]
    public void NoClockEstimateRendersEmptyFill()
    {
        using var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));
        client.OutboundSinkForTests = (_, _) => { };
        var decals = new List<TelegraphDecalState>();

        client.HandleMessageForTests(Slam(3, 1000, 1030));

        client.CopyTelegraphDecalsTo(decals, Ms(9999));
        var decal = Assert.Single(decals);
        Assert.Equal(0d, decal.Progress, 9);
        Assert.False(decal.Resolved);
    }
}
