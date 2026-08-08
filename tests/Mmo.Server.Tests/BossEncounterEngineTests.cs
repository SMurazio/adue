using System.Collections.Generic;
using System.Linq;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;
using EncounterState = Mmo.Server.Runtime.BossEncounterEngine.EncounterState;

namespace Mmo.Server.Tests;

// BOSS-1 (docs/boss-encounter-sunderer-design.md): headless tests for the Sunderer encounter lifecycle engine —
// driven exactly as GameServer wires it (every world touch is an injected seam over a bare WorldState), in the SAME
// per-tick shape TickCore runs (TryBegin/TryLeave on the command path, Step once per tick). These pin the lifecycle
// contract: the 3 s countdown transitions to a spawned boss; HP scales 1200 duo / 700 solo at spawn; a full-party
// death resets immediately (wipe); an emptied arena resets only after the 10 s grace window; boss death is a victory
// that returns to Idle while retaining the victors; and the enter/leave teleport bookkeeping restores return tiles.
public sealed class BossEncounterEngineTests
{
    private const int TickRate = 20;
    private static readonly TileCoord TownA = new(190, 40);
    private static readonly TileCoord TownB = new(196, 40);

    // The headless harness: the engine wired over a real WorldState with recording lambdas for every seam (spawn /
    // despawn / resolve / teleport / notify) — the same seams GameServer supplies from SpawnMonsterCore / a leak-free
    // despawn / Zone.Teleport / SendSystem, but recorded so the tests can assert HP scaling, teleport targets, and
    // chat.
    private sealed class Harness
    {
        public readonly WorldState World = new();
        public readonly List<(ulong Id, string Text)> Announcements = [];
        public readonly List<(ulong Id, TileCoord Tile)> Teleports = [];
        public readonly BossEncounterEngine Engine;
        public WorldEntity? Boss;
        public int? BossSpawnHealth;
        public bool BossDespawned;

        // BOSS-2 (P1): drone + plating recorders.
        public WorldEntity? Drone;
        public int DroneSpawnCount;
        public readonly List<ulong> AddDespawns = [];
        public readonly List<(ulong BossId, bool Active)> PlatingBroadcasts = [];

        // BOSS-3 (P2): recorders. PlayerDamage is the REAL PlayerDamageGate's landed tail (the engine routes every
        // field/lash/pop hit through the genuine choke-point method group — no i-frame oracle + no shield seam wired
        // here, so the gate's dead-guard + ApplyDamage + landed tail are exercised; the i-frame/shield behaviour is
        // pinned by PlayerDamageGate/TelegraphScheduler's own suites, not re-verified here).
        public readonly List<(ulong Id, int Amount, string Source)> PlayerDamage = [];
        public readonly List<(ulong Id, WorldVector To)> Displacements = [];
        public readonly List<ulong> EchoCues = [];
        public int SplinterSpawnCount;
        public readonly List<(WorldVector Center, double Radius, uint Start, uint Resolve)> FieldVisuals = [];

        // BOSS-4 (P3): recorders. RootCalls records each root (boss re-centre) at the P3 edge; the root also teleports
        // the boss so its Position reflects centre for beam-origin / ward-distance math. Beams records each scheduled
        // sweep-beam LINE telegraph (origin/aim/damage/ticks) — the SEQUENTIAL rotating beam is a recorder here, exactly
        // as BOSS-3 recorded field visuals (the real TelegraphScheduler gate is pinned by its own suite).
        public readonly List<(ulong BossId, TileCoord Tile)> RootCalls = [];
        public readonly List<(WorldVector Origin, double Length, double Aim, double HalfWidth, int Damage, uint Start, uint Resolve)> Beams = [];

        private uint _nextNetworkId = 1000;

        public Harness()
        {
            var gate = new PlayerDamageGate(
                hasActiveIFrames: (_, _) => false,
                onDamageLanded: (victim, amount, source) => PlayerDamage.Add((victim.Id, amount, source)));

            Engine = new BossEncounterEngine(
                TickRate,
                spawnBoss: (tile, maxHealth) =>
                {
                    var boss = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "The Sunderer", tile, Direction8.S);
                    boss.SetMaxHealthFull(maxHealth);
                    Boss = boss;
                    BossSpawnHealth = maxHealth;
                    BossDespawned = false;
                    return boss;
                },
                despawnBoss: id =>
                {
                    if (World.Remove(id, out _))
                    {
                        BossDespawned = true;
                    }
                },
                tryResolve: id => World.TryGet(id, out var e) ? e : null,
                teleport: (player, tile) =>
                {
                    Teleports.Add((player.Id, tile));
                    player.TeleportTo(tile);
                },
                notify: (id, text) => Announcements.Add((id, text)),
                spawnDrone: tile =>
                {
                    var drone = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "Interposer Drone", tile, Direction8.S);
                    drone.SetMaxHealthFull(40);
                    Drone = drone;
                    DroneSpawnCount++;
                    return drone;
                },
                despawnAdd: id =>
                {
                    if (World.Remove(id, out _))
                    {
                        AddDespawns.Add(id);
                    }
                },
                broadcastPlating: (bossId, active) => PlatingBroadcasts.Add((bossId, active)),
                // BOSS-3 (P2): route damage through the REAL PlayerDamageGate (the choke-point method group), record
                // displacement/echo/splinter-spawn/field-visual seams like the P1 recorders.
                damagePlayer: gate.TryDamagePlayer,
                displacePlayer: (player, to) =>
                {
                    Displacements.Add((player.Id, to));
                    var previous = player.TileCoord;
                    if (player.ApplyResolvedMove(to))
                    {
                        World.OnEntityMoved(player, previous);
                    }
                },
                echoCue: id => EchoCues.Add(id),
                spawnSplinter: tile =>
                {
                    var splinter = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "Splinter", tile, Direction8.S);
                    splinter.SetMaxHealthFull(15);
                    SplinterSpawnCount++;
                    return splinter;
                },
                scheduleFieldVisual: (center, radius, start, resolve) =>
                    FieldVisuals.Add((center, radius, start, resolve)),
                // BOSS-4 (P3 root): record + perform the re-centre (teleport the boss to `tile` + zero its velocity), so
                // its Position reflects centre for the beam-origin / ward-distance assertions below.
                rootBoss: (boss, tile) =>
                {
                    RootCalls.Add((boss.Id, tile));
                    boss.TeleportTo(tile);
                    boss.StopMovement();
                },
                // BOSS-4 (P3 sweep beam): record each scheduled LINE telegraph.
                scheduleBeam: (origin, length, aim, halfWidth, damage, start, resolve) =>
                    Beams.Add((origin, length, aim, halfWidth, damage, start, resolve)));
        }

        // BOSS-2 (P1): begin a fight and run the 3 s countdown so the boss is spawned + Active. Participants enter at
        // the interior entry tiles (so the wipe check sees them alive-in-arena and the fight stays Active while stepped).
        public WorldEntity[] BeginAndSpawnBoss(bool duo)
        {
            var issuer = AddPlayer("Issuer", TownA);
            WorldEntity? partner = duo ? AddPlayer("Partner", TownB) : null;
            Engine.TryBegin(issuer, partner, serverTick: 0, out _);
            StepThrough(1, 60);
            Assert.Equal(EncounterState.Active, Engine.State);
            return partner is null ? [issuer] : [issuer, partner];
        }

        public WorldEntity AddPlayer(string name, TileCoord tile) =>
            World.AddTransient(_nextNetworkId++, EntityKind.Player, name, tile, Direction8.S);

        // Drive the engine from `from` to `to` inclusive (the per-tick Step pump).
        public void StepThrough(uint from, uint to)
        {
            for (var t = from; t <= to; t++)
            {
                Engine.Step(t);
            }
        }

        // BOSS-3 (P2): drop the boss to `fraction` of max HP, then Step(atTick) so the plating crumbles (fraction must
        // be <=70% and >40% to LAND in P2). The crumble ANCHORS every P2 cadence at `atTick`; returns it.
        public uint CrumbleIntoP2(uint atTick, double fraction = 0.65d)
        {
            var boss = Boss!;
            var target = (int)(boss.Stats.MaxHealth * fraction);
            boss.ApplyDamage(boss.Stats.Health - target);
            Engine.Step(atTick);
            Assert.True(Engine.P2Active, "test setup: the boss did not enter P2 at the crumble tick.");
            return atTick;
        }

        // BOSS-4 (P3): drop the boss through P2 (at atTick-1) then across the 40% edge (at atTick) so P3 ARMS at atTick
        // (the P3 anchor T0). Leaves the boss at 35% HP (above the 10% enrage edge). Returns T0.
        public uint CrumbleIntoP3(uint atTick)
        {
            CrumbleIntoP2(atTick - 1, fraction: 0.65d);
            var boss = Boss!;
            boss.ApplyDamage(boss.Stats.Health - (int)(boss.Stats.MaxHealth * 0.35d)); // to 35%.
            Engine.Step(atTick);
            Assert.True(Engine.P3Active, "test setup: the boss did not enter P3 at the edge tick.");
            return atTick;
        }

        // Reposition a participant to a continuous point, keeping the spatial bucket in sync (the field-distance primitive).
        public void MoveTo(WorldEntity e, WorldVector position)
        {
            var previous = e.TileCoord;
            if (e.ApplyResolvedMove(position))
            {
                World.OnEntityMoved(e, previous);
            }
        }
    }

    // ==== BOSS-3 (P2 SUNDER): Repel/Bind fields + Echo Lash + splinter ring ====

    // The interior-centre reference the field tests position participants around (well inside the 22×22 arena so a 3u
    // knockback never leaves it and the wipe/prune checks keep the fight Active).
    private static readonly WorldVector ArenaCentre =
        new((BossArena.InteriorMinX + BossArena.InteriorMaxX) / 2d, (BossArena.InteriorMinY + BossArena.InteriorMaxY) / 2d);

    [Fact]
    public void Fields_ArmAtCrumble_TelegraphThenAlternateRepelBind()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP2(atTick: 100);
        Assert.Contains(h.Announcements, a => a.Text.Contains("crumbles for good"));

        // First field fires 6 s (120 t) after the crumble, telegraphs for 1.2 s (24 t), resolves at +144. A ring decal
        // is scheduled around EACH of the 2 participants at fire.
        h.StepThrough(t0 + 1, t0 + 120);
        Assert.Equal(2, h.FieldVisuals.Count);
        Assert.All(h.FieldVisuals, v => Assert.Equal(t0 + 144, v.Resolve));
        Assert.Contains(h.Announcements, a => a.Text.Contains("REPELS")); // first field is Repel.

        // Second field is BIND, one interval (9 s = 180 t) after the first fire.
        h.StepThrough(t0 + 121, t0 + 300);
        Assert.Equal(4, h.FieldVisuals.Count);
        Assert.Contains(h.Announcements, a => a.Text.Contains("BINDS"));
    }

    [Fact]
    public void RepelField_DamagesAndKnocksApart_OnlyWhenTooClose()
    {
        // Too close at resolve → both take 15 + are shoved 3u apart.
        var close = new Harness();
        var cp = close.BeginAndSpawnBoss(duo: true);
        var t0 = close.CrumbleIntoP2(atTick: 100);
        close.MoveTo(cp[0], ArenaCentre + new WorldVector(-1d, 0d));
        close.MoveTo(cp[1], ArenaCentre + new WorldVector(1d, 0d)); // 2u apart (<=6u).
        close.StepThrough(t0 + 1, t0 + 144); // fire @+120, resolve @+144.
        Assert.Equal(2, close.PlayerDamage.Count(d => d.Source == "Repel field"));
        Assert.All(close.PlayerDamage.Where(d => d.Source == "Repel field"), d => Assert.Equal(15, d.Amount));
        Assert.Equal(2, close.Displacements.Count); // both knocked apart.

        // Far apart at resolve → the Repel does nothing (no damage, no displacement).
        var far = new Harness();
        var fp = far.BeginAndSpawnBoss(duo: true);
        var f0 = far.CrumbleIntoP2(atTick: 100);
        far.MoveTo(fp[0], ArenaCentre + new WorldVector(-5d, 0d));
        far.MoveTo(fp[1], ArenaCentre + new WorldVector(5d, 0d)); // 10u apart (>6u).
        far.StepThrough(f0 + 1, f0 + 144);
        Assert.Empty(far.PlayerDamage.Where(d => d.Source == "Repel field"));
        Assert.Empty(far.Displacements);
    }

    [Fact]
    public void BindField_Damages_OnlyWhenTooFar_AndNeverDisplaces()
    {
        // The 2nd field is Bind. Far apart at its resolve → both take 15, NO displacement.
        var far = new Harness();
        var fp = far.BeginAndSpawnBoss(duo: true);
        var f0 = far.CrumbleIntoP2(atTick: 100);
        // Keep them close through the 1st (Repel) field so it neither damages-far nor perturbs the setup...
        far.MoveTo(fp[0], ArenaCentre + new WorldVector(-1d, 0d));
        far.MoveTo(fp[1], ArenaCentre + new WorldVector(1d, 0d));
        far.StepThrough(f0 + 1, f0 + 200); // past the 1st field's resolve (+144); 2nd fires @+300.
        far.PlayerDamage.Clear();
        far.Displacements.Clear();
        // Now spread them out for the Bind resolve (2nd field fire +300, resolve +324).
        far.MoveTo(fp[0], ArenaCentre + new WorldVector(-5d, 0d));
        far.MoveTo(fp[1], ArenaCentre + new WorldVector(5d, 0d)); // 10u apart (>4u).
        far.StepThrough(f0 + 201, f0 + 324);
        Assert.Equal(2, far.PlayerDamage.Count(d => d.Source == "Bind field"));
        Assert.Empty(far.Displacements); // Bind never knocks.

        // Together at the Bind resolve → nothing. (The 1st Repel field knocks them apart at +144, so re-close them
        // before the 2nd field's Bind resolve.)
        var near = new Harness();
        var np = near.BeginAndSpawnBoss(duo: true);
        var n0 = near.CrumbleIntoP2(atTick: 100);
        near.MoveTo(np[0], ArenaCentre + new WorldVector(-1d, 0d));
        near.MoveTo(np[1], ArenaCentre + new WorldVector(1d, 0d));
        near.StepThrough(n0 + 1, n0 + 200); // past the 1st (Repel) field's resolve.
        near.PlayerDamage.Clear();
        near.MoveTo(np[0], ArenaCentre + new WorldVector(-1d, 0d));
        near.MoveTo(np[1], ArenaCentre + new WorldVector(1d, 0d)); // 2u apart for the Bind resolve.
        near.StepThrough(n0 + 201, n0 + 324);
        Assert.Empty(near.PlayerDamage.Where(d => d.Source == "Bind field"));
    }

    [Fact]
    public void SoloField_IsMoveOutVsBoss_RepelOnly()
    {
        var h = new Harness();
        var p = h.BeginAndSpawnBoss(duo: false);
        var t0 = h.CrumbleIntoP2(atTick: 100);
        var boss = h.Boss!;
        // Stand within 6u of the boss at the resolve → damage + knockback away from the boss.
        h.MoveTo(p[0], boss.Position + new WorldVector(2d, 0d));
        h.StepThrough(t0 + 1, t0 + 144);
        Assert.Single(h.PlayerDamage, d => d.Source == "Sunder field" && d.Amount == 15);
        Assert.Single(h.Displacements);
        // Shoved AWAY from the boss (position moved further out along +x).
        Assert.True(h.Displacements[0].To.X > boss.Position.X + 2d);
    }

    [Fact]
    public void EchoLash_CueThenTwoPulses_HalfSecondApart_LivingOnly()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP2(atTick: 100);
        // Keep them spread beyond the Repel/Bind bands is irrelevant to the lash; just avoid a wipe (huge HP).
        players[0].SetMaxHealthFull(100000);
        players[1].SetMaxHealthFull(100000);

        // Cue @ +220; pulse1 @ +245 (cue + 1.25 s); pulse2 @ +255 (+0.5 s). Cue reaches BOTH participants.
        h.StepThrough(t0 + 1, t0 + 244);
        Assert.Equal(2, h.EchoCues.Count(id => id == players[0].Id || id == players[1].Id));
        Assert.Contains(h.Announcements, a => a.Text.Contains("brace as one"));
        Assert.Empty(h.PlayerDamage.Where(d => d.Source == "Echo Lash")); // not until +245.

        h.Engine.Step(t0 + 245);
        Assert.Equal(2, h.PlayerDamage.Count(d => d.Source == "Echo Lash")); // pulse 1 hits both living.
        h.StepThrough(t0 + 246, t0 + 254);
        Assert.Equal(2, h.PlayerDamage.Count(d => d.Source == "Echo Lash")); // still just pulse 1.
        h.Engine.Step(t0 + 255);
        Assert.Equal(4, h.PlayerDamage.Count(d => d.Source == "Echo Lash")); // pulse 2.
        Assert.All(h.PlayerDamage.Where(d => d.Source == "Echo Lash"), d => Assert.Equal(18, d.Amount));

        // Solo: a SINGLE pulse.
        var solo = new Harness();
        var s = solo.BeginAndSpawnBoss(duo: false);
        var s0 = solo.CrumbleIntoP2(atTick: 100);
        s[0].SetMaxHealthFull(100000);
        solo.StepThrough(s0 + 1, s0 + 260);
        Assert.Single(solo.PlayerDamage, d => d.Source == "Echo Lash");
    }

    [Fact]
    public void SplinterRing_SpawnsSixDuo_ThreeSolo_AndReRings()
    {
        var duo = new Harness();
        var players = duo.BeginAndSpawnBoss(duo: true);
        // This test spans 28 s of live P2 — the harness routes field/lash damage through the REAL PlayerDamageGate,
        // and two players standing motionless at the entry tiles accumulate a genuine WIPE (~117 damage) before the
        // re-ring, resetting the encounter (correct engine behavior that failed this test's original draft). Tanky
        // HP keeps the fight alive; wipe semantics have their own test.
        foreach (var player in players)
        {
            player.SetMaxHealthFull(100_000);
        }

        var d0 = duo.CrumbleIntoP2(atTick: 100);
        // First ring 8 s (160 t) after the crumble.
        duo.StepThrough(d0 + 1, d0 + 159);
        Assert.Equal(0, duo.SplinterSpawnCount);
        duo.Engine.Step(d0 + 160);
        Assert.Equal(6, duo.SplinterSpawnCount);
        Assert.Equal(6, duo.Engine.AddCount);
        // Re-rings every 20 s (400 t) → +560.
        duo.StepThrough(d0 + 161, d0 + 559);
        Assert.Equal(6, duo.SplinterSpawnCount);
        duo.Engine.Step(d0 + 560);
        Assert.Equal(12, duo.SplinterSpawnCount);

        var solo = new Harness();
        solo.BeginAndSpawnBoss(duo: false);
        var s0 = solo.CrumbleIntoP2(atTick: 100);
        solo.StepThrough(s0 + 1, s0 + 160);
        Assert.Equal(3, solo.SplinterSpawnCount);
    }

    [Fact]
    public void Splinter_PopsWithinOneUnit_DamagesParticipant_AndDespawns()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP2(atTick: 100);
        h.StepThrough(t0 + 1, t0 + 160); // ring spawns.
        Assert.Equal(6, h.Engine.AddCount);

        // Drag one splinter onto a participant (within 1u) — it pops on the next Step.
        var splinter = h.World.Entities.Where(e => e.DisplayName == "Splinter" && e.Stats.Health > 0)
            .OrderBy(e => e.Id).First();
        h.MoveTo(splinter, players[0].Position + new WorldVector(0.5d, 0d));
        var before = h.PlayerDamage.Count;
        h.Engine.Step(t0 + 161);
        Assert.Equal(before + 1, h.PlayerDamage.Count);
        var pop = h.PlayerDamage[^1];
        Assert.Equal(players[0].Id, pop.Id);
        Assert.Equal("Splinter", pop.Source);
        Assert.Equal(18, pop.Amount); // feel-test tune: pop 12→18 (splinters were too weak/slow).
        Assert.Contains(splinter.Id, h.AddDespawns);
        Assert.Equal(5, h.Engine.AddCount); // popped splinter left the ledger.
    }

    [Fact]
    public void Splinters_TornDown_OnWipe_Victory_AndWalkOut()
    {
        // Wipe: both die in-arena → immediate reset tears the ring down.
        var wipe = new Harness();
        var wp = wipe.BeginAndSpawnBoss(duo: true);
        var w0 = wipe.CrumbleIntoP2(atTick: 100);
        wipe.StepThrough(w0 + 1, w0 + 160);
        Assert.Equal(6, wipe.Engine.AddCount);
        wp[0].ApplyDamage(wp[0].Stats.Health);
        wp[1].ApplyDamage(wp[1].Stats.Health);
        wipe.Engine.Step(w0 + 161);
        Assert.Equal(EncounterState.Idle, wipe.Engine.State);
        Assert.Equal(0, wipe.Engine.AddCount);
        Assert.Equal(6, wipe.AddDespawns.Count);

        // Victory: boss removed → adds torn down, victors retained.
        var victory = new Harness();
        victory.BeginAndSpawnBoss(duo: true);
        var v0 = victory.CrumbleIntoP2(atTick: 100);
        victory.StepThrough(v0 + 1, v0 + 160);
        Assert.True(victory.World.Remove(victory.Engine.BossId, out _));
        victory.Engine.Step(v0 + 161);
        Assert.Equal(EncounterState.Idle, victory.Engine.State);
        Assert.Equal(0, victory.Engine.AddCount);
        Assert.Equal(6, victory.AddDespawns.Count);

        // Walk-out (solo leaves → empty → grace reset) tears the ring down.
        var walk = new Harness();
        var lone = walk.BeginAndSpawnBoss(duo: false);
        var l0 = walk.CrumbleIntoP2(atTick: 100);
        walk.StepThrough(l0 + 1, l0 + 160);
        Assert.Equal(3, walk.Engine.AddCount);
        Assert.True(walk.Engine.TryLeave(lone[0], out _));
        walk.StepThrough(l0 + 161, l0 + 161 + 200); // arm + 10 s grace.
        Assert.Equal(EncounterState.Idle, walk.Engine.State);
        Assert.Equal(0, walk.Engine.AddCount);
        Assert.Equal(3, walk.AddDespawns.Count);
    }

    [Fact]
    public void P2_DisarmsAtFortyPercent_ClearsSplinters_AndTeases()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP2(atTick: 100);
        h.StepThrough(t0 + 1, t0 + 160); // ring up.
        Assert.Equal(6, h.Engine.AddCount);

        // Drop the boss to exactly 40% (480/1200): P2 disarms on the next Step.
        h.Boss!.ApplyDamage(h.Boss!.Stats.Health - 480);
        h.Engine.Step(t0 + 161);
        Assert.False(h.Engine.P2Active);
        Assert.True(h.Engine.P3Reached);
        Assert.Equal(0, h.Engine.AddCount);
        Assert.Equal(6, h.AddDespawns.Count); // existing splinters die at 40%.
        Assert.Contains(h.Announcements, a => a.Text.Contains("shattered mass inward"));

        // No more rings below 40%.
        h.StepThrough(t0 + 162, t0 + 700);
        Assert.Equal(6, h.SplinterSpawnCount); // never re-rang.
    }

    [Fact]
    public void NoP2Activity_AboveSeventy_OrOutsideActive()
    {
        // Above 70%: the boss stays full-HP; P2 never arms (no fields/lash/ring across a long run).
        var above = new Harness();
        above.BeginAndSpawnBoss(duo: true);
        above.StepThrough(61, 900);
        Assert.False(above.Engine.P2Active);
        Assert.Empty(above.FieldVisuals);
        Assert.Equal(0, above.SplinterSpawnCount);
        Assert.Empty(above.EchoCues);
        Assert.Empty(above.PlayerDamage);

        // Countdown/Idle: a would-be P2 tick never runs (no boss, not Active).
        var countdown = new Harness();
        var issuer = countdown.AddPlayer("Solo", TownA);
        countdown.Engine.TryBegin(issuer, partner: null, serverTick: 0, out _);
        countdown.StepThrough(1, 40);
        Assert.False(countdown.Engine.P2Active);
        Assert.Empty(countdown.FieldVisuals);
    }

    [Fact]
    public void StaggerConstants_FieldResolve_LashPulse_RingSpawn_NeverShareATick()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP2(atTick: 137); // an arbitrary (odd) anchor — the disjointness must hold for ANY anchor.
        // Keep everyone alive + spread so pulses always land and no self-wipe (huge HP; far from boss so no pops).
        players[0].SetMaxHealthFull(10_000_000);
        players[1].SetMaxHealthFull(10_000_000);
        h.MoveTo(players[0], ArenaCentre + new WorldVector(-5d, 0d));
        h.MoveTo(players[1], ArenaCentre + new WorldVector(5d, 0d));

        var lashPulseTicks = new List<uint>();
        var ringSpawnTicks = new List<uint>();
        var lashSeen = 0;
        var ringSeen = 0;
        for (var t = t0 + 1; t <= t0 + 1200; t++) // 60 s of P2.
        {
            h.Engine.Step(t);
            var lashNow = h.PlayerDamage.Count(d => d.Source == "Echo Lash");
            if (lashNow > lashSeen)
            {
                lashPulseTicks.Add(t);
                lashSeen = lashNow;
            }

            if (h.SplinterSpawnCount > ringSeen)
            {
                ringSpawnTicks.Add(t);
                ringSeen = h.SplinterSpawnCount;
            }
        }

        // Field RESOLVE ticks are the scheduled visual deadlines (fire + telegraph) — one per fire, deduped.
        var fieldResolveTicks = h.FieldVisuals.Select(v => v.Resolve).Distinct().ToHashSet();
        var lashSet = lashPulseTicks.ToHashSet();
        var ringSet = ringSpawnTicks.ToHashSet();

        Assert.NotEmpty(fieldResolveTicks);
        Assert.NotEmpty(lashSet);
        Assert.NotEmpty(ringSet);
        // Pairwise disjoint — no two of the three damage/spawn streams ever resolve on the same tick, by construction.
        Assert.Empty(fieldResolveTicks.Intersect(lashSet));
        Assert.Empty(fieldResolveTicks.Intersect(ringSet));
        Assert.Empty(lashSet.Intersect(ringSet));
    }

    [Fact]
    public void TryBegin_TeleportsToEntryTiles_AndEntersCountdown()
    {
        var h = new Harness();
        var issuer = h.AddPlayer("Issuer", TownA);
        var partner = h.AddPlayer("Partner", TownB);

        Assert.True(h.Engine.TryBegin(issuer, partner, serverTick: 100, out var msg));
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
        Assert.Equal(2, h.Engine.ParticipantCount);
        Assert.Null(h.Boss); // no boss until the countdown ends.

        // Both were teleported to the fixed entry tiles; the issuer got a chat line.
        Assert.Equal(BossArena.IssuerEntryTile, issuer.TileCoord);
        Assert.Equal(BossArena.PartnerEntryTile, partner.TileCoord);
        Assert.Contains("arena", msg);
        // The partner got its own pulled-in line.
        Assert.Contains(h.Announcements, a => a.Id == partner.Id && a.Text.Contains("pulled you"));
    }

    [Fact]
    public void Countdown_SpawnsBossAtCentre_AfterThreeSeconds()
    {
        var h = new Harness();
        var issuer = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(issuer, partner: null, serverTick: 100, out _);

        // The 3 s countdown = 60 ticks at 20 Hz → spawn at tick 160. Strictly before: still counting, no boss.
        h.StepThrough(101, 159);
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
        Assert.Null(h.Boss);

        h.Engine.Step(160);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        Assert.NotNull(h.Boss);
        Assert.Equal(BossArena.BossSpawnTile, h.Boss!.TileCoord);
        // The whole-second countdown was announced (3.. 2.. 1..) plus the awaken line.
        Assert.Contains(h.Announcements, a => a.Text.Contains("3"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("2"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("1"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("awakens"));
    }

    [Fact]
    public void BossHealth_ScalesWithParticipantCount()
    {
        // Duo → 1200.
        var duo = new Harness();
        var a = duo.AddPlayer("A", TownA);
        var b = duo.AddPlayer("B", TownB);
        duo.Engine.TryBegin(a, b, serverTick: 0, out _);
        duo.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, duo.Engine.State);
        Assert.Equal(BossEncounterEngine.DuoBossHealth, duo.BossSpawnHealth);
        Assert.Equal(1200, duo.Boss!.Stats.MaxHealth);
        Assert.Equal(1200, duo.Boss!.Stats.Health);

        // Solo → 700.
        var solo = new Harness();
        var s = solo.AddPlayer("Solo", TownA);
        solo.Engine.TryBegin(s, partner: null, serverTick: 0, out _);
        solo.StepThrough(1, 60);
        Assert.Equal(BossEncounterEngine.SoloBossHealth, solo.BossSpawnHealth);
        Assert.Equal(700, solo.Boss!.Stats.MaxHealth);
    }

    [Fact]
    public void AllParticipantsDead_ResetsImmediately()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        var bossId = h.Boss!.Id;

        // Both die in the arena (Health → 0). This is the pre-RespawnPlayers window the engine's Step runs in.
        a.ApplyDamage(a.Stats.Health);
        b.ApplyDamage(b.Stats.Health);
        Assert.True(a.Stats.Health <= 0 && b.Stats.Health <= 0);

        h.Engine.Step(61);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.True(h.BossDespawned);
        Assert.False(h.World.TryGet(bossId, out _)); // boss removed on wipe.
    }

    [Fact]
    public void ArenaEmptied_ResetsOnlyAfterTheGraceWindow()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, h.Engine.State);

        // Leave the arena → participants empty; the boss is abandoned (still alive, so NOT a victory).
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.Equal(0, h.Engine.ParticipantCount);

        // The 10 s grace = 200 ticks. Arm at 61; still Active right up to 260; reset exactly at 261 (200 elapsed).
        h.Engine.Step(61);
        h.StepThrough(62, 260);
        Assert.Equal(EncounterState.Active, h.Engine.State);

        h.Engine.Step(261);
        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.True(h.BossDespawned);
    }

    [Fact]
    public void Disconnect_DuringActive_EmptiesThenResetsAfterGrace()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        h.StepThrough(1, 60);

        // Disconnect: the entity vanishes from the world (unresolvable). Prune drops it → empty → grace timer.
        Assert.True(h.World.Remove(a.Id, out _));
        h.Engine.Step(61);
        h.StepThrough(62, 260);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        h.Engine.Step(261);
        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.True(h.BossDespawned);
    }

    [Fact]
    public void BossDeath_IsVictory_ReturnsToIdle_RetainingVictors()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        var bossId = h.Boss!.Id;

        // Simulate a player kill: KillMonster despawns the boss, so the engine resolves it to null → victory.
        Assert.True(h.World.Remove(bossId, out _));
        h.Engine.Step(61);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Contains(h.Announcements, m => m.Text.Contains("Victory"));
        // Victors are RETAINED so they can walk out via /boss (no auto-eject).
        Assert.Equal(2, h.Engine.ParticipantCount);

        // A fresh /boss is refused while the victors still occupy the arena...
        var c = h.AddPlayer("C", TownA);
        Assert.False(h.Engine.TryBegin(c, partner: null, serverTick: 62, out var deny));
        Assert.Contains("occupied", deny);

        // ...but each victor can leave (teleported home), and then a new encounter can begin.
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.True(h.Engine.TryLeave(b, out _));
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.True(h.Engine.TryBegin(c, partner: null, serverTick: 63, out _));
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
    }

    [Fact]
    public void VictorsWhoNeverLeave_AreEjectedHomeAfterTheGraceWindow_FreeingTheArena()
    {
        // Review BOSS-1 (MEDIUM): the arena is shared and non-instanced — a connected victor who idles inside must
        // NOT hold /boss hostage forever. 15 s after victory, stragglers are teleported to their stored return tiles
        // and the arena frees up.
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.True(h.World.Remove(h.Boss!.Id, out _)); // player kill → victory on the next Step.
        h.Engine.Step(61);
        Assert.Equal(2, h.Engine.ParticipantCount);

        // Just before the 15 s deadline (armed at tick 61 → deadline 61 + 15*20 = 361): still retained.
        h.StepThrough(62, 360);
        Assert.Equal(2, h.Engine.ParticipantCount);
        Assert.Equal(BossArena.IssuerEntryTile, a.TileCoord);

        // Deadline reached: both stragglers are sent to their ORIGINAL return tiles and the arena clears.
        h.Engine.Step(361);
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.Equal(TownA, a.TileCoord);
        Assert.Equal(TownB, b.TileCoord);

        // The arena is genuinely free again: a fresh /boss starts a new countdown.
        var c = h.AddPlayer("C", TownA);
        Assert.True(h.Engine.TryBegin(c, partner: null, serverTick: 362, out _));
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
    }

    [Fact]
    public void TryLeave_ReturnsEachPlayerToItsStoredTile()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);

        // Entered → at the entry tiles.
        Assert.Equal(BossArena.IssuerEntryTile, a.TileCoord);
        Assert.Equal(BossArena.PartnerEntryTile, b.TileCoord);

        // Leaving returns each to the ORIGINAL tile it entered from.
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.Equal(TownA, a.TileCoord);
        Assert.True(h.Engine.TryLeave(b, out _));
        Assert.Equal(TownB, b.TileCoord);
    }

    [Fact]
    public void TryBegin_WhileAnEncounterIsInProgress_IsDenied()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);

        Assert.True(h.Engine.TryBegin(a, partner: null, serverTick: 0, out _));
        Assert.False(h.Engine.TryBegin(b, partner: null, serverTick: 1, out var msg));
        Assert.Contains("already engaged", msg);
    }

    [Fact]
    public void Countdown_CancelsToIdle_WhenEveryoneLeavesBeforeSpawn()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        Assert.Equal(EncounterState.Countdown, h.Engine.State);

        // Leave mid-countdown → next Step finds the arena empty and cancels (no boss ever spawns).
        Assert.True(h.Engine.TryLeave(a, out _));
        h.Engine.Step(1);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Null(h.Boss);
        Assert.False(h.BossDespawned);
    }

    // ==== BOSS-2 (P1 HUSK): Sundered Plating + fusion shatter + interposer drone ====

    [Fact]
    public void Plating_ReducesDamage_DuoSeventyFive_SoloForty_AndTauntsOnce()
    {
        var duo = new Harness();
        var players = duo.BeginAndSpawnBoss(duo: true);
        var bossId = duo.Engine.BossId;
        Assert.True(duo.Engine.PlatingActive);
        // Spawn broadcast the shell is UP.
        Assert.Contains(duo.PlatingBroadcasts, p => p.BossId == bossId && p.Active);

        Assert.Equal(25, duo.Engine.ModifyIncomingDamage(bossId, 100)); // 75% reduction (duo).
        // The one-shot "your blows turn" taunt is a once-per-FIGHT event, announced to EVERY participant (AnnounceAll)
        // — so exactly one record per player, and a second plated hit adds none.
        foreach (var player in players)
        {
            Assert.Single(duo.Announcements, a => a.Id == player.Id && a.Text.Contains("turns your blows"));
        }

        Assert.Equal(25, duo.Engine.ModifyIncomingDamage(bossId, 100));
        foreach (var player in players)
        {
            Assert.Single(duo.Announcements, a => a.Id == player.Id && a.Text.Contains("turns your blows"));
        }

        // A non-boss monster is never modified.
        Assert.Equal(100, duo.Engine.ModifyIncomingDamage(bossId + 999, 100));

        var solo = new Harness();
        solo.BeginAndSpawnBoss(duo: false);
        Assert.Equal(60, solo.Engine.ModifyIncomingDamage(solo.Engine.BossId, 100)); // 40% reduction (solo).
    }

    [Fact]
    public void FusionShatter_MakesDamageFull_ThenReforms_ToReduced()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;

        h.Engine.OnFusion(ProjectileTier.Good, 61);
        Assert.True(h.Engine.WindowOpen);
        Assert.False(h.Engine.PlatingActive); // shell shattered.
        Assert.Contains(h.Announcements, a => a.Text.Contains("SHATTERS"));
        Assert.Contains(h.PlatingBroadcasts, p => p.BossId == bossId && !p.Active);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100)); // full damage during the window.

        // Good window = 6 s = 120 ticks: still open at 180, reforms exactly at 181 (61 + 120).
        h.StepThrough(62, 180);
        Assert.True(h.Engine.WindowOpen);
        h.Engine.Step(181);
        Assert.False(h.Engine.WindowOpen);
        Assert.True(h.Engine.PlatingActive);
        Assert.Contains(h.Announcements, a => a.Text.Contains("reforms"));
        Assert.Equal(25, h.Engine.ModifyIncomingDamage(bossId, 100)); // reduced again.
    }

    [Fact]
    public void FusionWindowLengths_Good6s_Perfect9s()
    {
        var good = new Harness();
        good.BeginAndSpawnBoss(duo: true);
        good.Engine.OnFusion(ProjectileTier.Good, 61);
        good.StepThrough(62, 180);
        Assert.True(good.Engine.WindowOpen); // 6 s = 120 ticks: open through 180.
        good.Engine.Step(181);
        Assert.False(good.Engine.WindowOpen);

        var perfect = new Harness();
        perfect.BeginAndSpawnBoss(duo: true);
        perfect.Engine.OnFusion(ProjectileTier.Perfect, 61);
        perfect.StepThrough(62, 240);
        Assert.True(perfect.Engine.WindowOpen); // 9 s = 180 ticks: open through 240.
        perfect.Engine.Step(241);
        Assert.False(perfect.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_ThreeHitsWithinSixSeconds_Shatters()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: false);
        var bossId = h.Engine.BossId;

        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 100);
        Assert.False(h.Engine.WindowOpen); // 2 hits — not yet.
        h.Engine.OnSkillshotMonsterHit(bossId, 120); // 3rd within 6 s of the first (120 - 61 = 59 ticks).
        Assert.True(h.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_HitsSpreadBeyondSixSeconds_DoNotShatter()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: false);
        var bossId = h.Engine.BossId;

        // Each successive hit is > 6 s (120 ticks) after the prior, so the window never holds 3 at once.
        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 190);
        h.Engine.OnSkillshotMonsterHit(bossId, 330);
        Assert.False(h.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_Counting_IsInertInDuo()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 62);
        h.Engine.OnSkillshotMonsterHit(bossId, 63);
        Assert.False(h.Engine.WindowOpen); // duo uses fusion, not hit-count.
    }

    [Fact]
    public void Fusion_DuringCountdownOrIdle_IsIgnored()
    {
        // Countdown: TryBegin but no boss yet.
        var countdown = new Harness();
        var issuer = countdown.AddPlayer("Solo", TownA);
        countdown.Engine.TryBegin(issuer, partner: null, serverTick: 0, out _);
        Assert.Equal(EncounterState.Countdown, countdown.Engine.State);
        countdown.Engine.OnFusion(ProjectileTier.Perfect, 5);
        Assert.False(countdown.Engine.WindowOpen);

        // Idle: never began.
        var idle = new Harness();
        idle.Engine.OnFusion(ProjectileTier.Good, 0);
        Assert.False(idle.Engine.WindowOpen);
    }

    [Fact]
    public void Plating_CrumblesPermanently_AtSeventyPercent_FullDamageAndDroneGone()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        // Let the first drone spawn (5 s = 100 ticks after the boss spawned at tick 60 → tick 160).
        h.StepThrough(61, 160);
        Assert.True(h.Engine.DroneAlive);
        var droneId = h.Engine.DroneId;

        // Drop the boss to exactly 70% of 1200 (= 840): the plating crumbles for good on the next Step.
        h.Boss!.ApplyDamage(1200 - 840);
        h.Engine.Step(161);

        Assert.True(h.Engine.PlatingPermanentlyOff);
        Assert.False(h.Engine.PlatingActive);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100)); // full damage below 70%.
        Assert.False(h.Engine.DroneAlive);
        Assert.Contains(droneId, h.AddDespawns); // the drone is torn down at the boundary.
        Assert.Contains(h.PlatingBroadcasts, p => p.BossId == bossId && !p.Active);
        Assert.Contains(h.Announcements, a => a.Text.Contains("crumbles for good"));

        // No respawn below 70%.
        h.StepThrough(162, 400);
        Assert.False(h.Engine.DroneAlive);
    }

    [Fact]
    public void Drone_SpawnsAfterFiveSeconds_AndRespawnsSixAfterDeath()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);

        // Boss spawned at tick 60; first drone scheduled 5 s (100 ticks) out → tick 160.
        h.StepThrough(61, 159);
        Assert.Equal(0, h.DroneSpawnCount);
        Assert.False(h.Engine.DroneAlive);
        h.Engine.Step(160);
        Assert.Equal(1, h.DroneSpawnCount);
        Assert.True(h.Engine.DroneAlive);

        // Kill the drone (player kill → it vanishes from the world); the engine detects it and arms a 6 s respawn.
        var droneId = h.Engine.DroneId;
        Assert.True(h.World.Remove(droneId, out _));
        h.Engine.Step(161);
        Assert.False(h.Engine.DroneAlive);

        // Respawn 6 s (120 ticks) after the death was detected → tick 281. Not before.
        h.StepThrough(162, 280);
        Assert.Equal(1, h.DroneSpawnCount);
        h.Engine.Step(281);
        Assert.Equal(2, h.DroneSpawnCount);
        Assert.True(h.Engine.DroneAlive);
    }

    [Fact]
    public void Drone_TornDown_OnWipe_AndOnVictory()
    {
        // Wipe: both participants die in the arena → immediate reset tears the drone down.
        var wipe = new Harness();
        var players = wipe.BeginAndSpawnBoss(duo: true);
        wipe.StepThrough(61, 160);
        var wipeDroneId = wipe.Engine.DroneId;
        Assert.True(wipe.Engine.DroneAlive);
        players[0].ApplyDamage(players[0].Stats.Health);
        players[1].ApplyDamage(players[1].Stats.Health);
        wipe.Engine.Step(161);
        Assert.Equal(EncounterState.Idle, wipe.Engine.State);
        Assert.False(wipe.Engine.DroneAlive);
        Assert.Contains(wipeDroneId, wipe.AddDespawns);

        // Victory: the boss dies (removed) → the drone is torn down while the victors are retained.
        var victory = new Harness();
        victory.BeginAndSpawnBoss(duo: true);
        victory.StepThrough(61, 160);
        var victoryDroneId = victory.Engine.DroneId;
        Assert.True(victory.Engine.DroneAlive);
        Assert.True(victory.World.Remove(victory.Engine.BossId, out _));
        victory.Engine.Step(161);
        Assert.Equal(EncounterState.Idle, victory.Engine.State);
        Assert.False(victory.Engine.DroneAlive);
        Assert.Contains(victoryDroneId, victory.AddDespawns);
    }

    // ==== BOSS-4 (P3 CORE): root + Core Ward + midpoint break + sweep beam + knockback pulses + enrage ====

    [Fact]
    public void P3_ArmsAtFortyPercent_RootsBoss_AndSealsWard()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;

        // Above 40%: P3 never armed (the boss is full HP, only just spawned).
        Assert.False(h.Engine.P3Active);
        Assert.False(h.Engine.IsBossRooted(bossId));

        var t0 = h.CrumbleIntoP3(atTick: 200);
        Assert.True(h.Engine.P3Active);
        Assert.True(h.Engine.WardUp);
        // The boss was re-centred (rooted) ONCE at the edge to the arena's TRUE centre (CoreRootTile, review
        // MEDIUM-2 — not the spawn tile, which sat 3 tiles north and left a south beam-safe band), and
        // IsBossRooted now gates ONLY this boss.
        Assert.Single(h.RootCalls, r => r.BossId == bossId && r.Tile == BossArena.CoreRootTile);
        Assert.True(h.Engine.IsBossRooted(bossId));
        Assert.False(h.Engine.IsBossRooted(bossId + 999));
        Assert.Contains(h.Announcements, a => a.Text.Contains("core seals"));
        // WARD legibility rides BossPlatingMessage: sealing broadcasts plating TRUE (steel tint).
        Assert.True(h.PlatingBroadcasts[^1].BossId == bossId && h.PlatingBroadcasts[^1].Active);
    }

    [Fact]
    public void Ward_ZeroesAllDamage_UnlessABurstWindowIsOpen()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // Ward up → ALL damage to the boss is zeroed (the uniform ModifyIncomingDamage hook every source funnels through).
        Assert.Equal(0, h.Engine.ModifyIncomingDamage(bossId, 100));
        Assert.Equal(0, h.Engine.ModifyIncomingDamage(bossId, 999));
        // A non-boss monster is never modified.
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId + 999, 100));

        // Break the ward with an on-centre blast → full damage during the 8 s window.
        h.Engine.OnMidpointBlast(h.Boss!.Position, t0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.True(h.Engine.BurstWindowOpen);
        Assert.False(h.Engine.WardUp);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100));

        // Window = 8 s = 160 ticks: still open at +160 (opened at t0+1 → ends t0+161), reforms exactly at t0+161.
        h.StepThrough(t0 + 2, t0 + 160);
        Assert.True(h.Engine.BurstWindowOpen);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100));
        h.Engine.Step(t0 + 161);
        Assert.False(h.Engine.BurstWindowOpen);
        Assert.True(h.Engine.WardUp);
        Assert.Equal(0, h.Engine.ModifyIncomingDamage(bossId, 100)); // ward zeroes again.
        Assert.Contains(h.Announcements, a => a.Text.Contains("reseals"));
    }

    [Fact]
    public void WardBreak_HonoursTheRadiusThreshold_DuoTwoAndAHalf_SoloThreeAndAHalf()
    {
        // Duo: 2.5u. Inside breaks; just outside does not. A qualifying duo-tier blast (Perfect, well-separated pair)
        // is used throughout so the radius is the only variable under test — the tier/separation gate is pinned
        // separately below (WardBreak_DuoMode_RequiresPairedTierAndMinimumSeparation).
        var inside = new Harness();
        inside.BeginAndSpawnBoss(duo: true);
        var i0 = inside.CrumbleIntoP3(atTick: 200);
        inside.Engine.OnMidpointBlast(inside.Boss!.Position + new WorldVector(2.4d, 0d), i0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.True(inside.Engine.BurstWindowOpen);

        var outside = new Harness();
        outside.BeginAndSpawnBoss(duo: true);
        var o0 = outside.CrumbleIntoP3(atTick: 200);
        outside.Engine.OnMidpointBlast(outside.Boss!.Position + new WorldVector(2.6d, 0d), o0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.False(outside.Engine.BurstWindowOpen);

        // Solo: the receiver-forgiving 3.5u. A blast at 3.4u breaks it; 3.6u does not. Solo mode is ungated by
        // tier/separation (< 2 participants at spawn), so this passes PairTier.None/0d — the exact report shape the
        // solo degradation self-blast produces (see MidpointDetonationEngineTests.SoloDegradation_...).
        var soloIn = new Harness();
        soloIn.BeginAndSpawnBoss(duo: false);
        var s0 = soloIn.CrumbleIntoP3(atTick: 200);
        soloIn.Engine.OnMidpointBlast(soloIn.Boss!.Position + new WorldVector(3.4d, 0d), s0 + 1, PairTier.None, 0d);
        Assert.True(soloIn.Engine.BurstWindowOpen);

        var soloOut = new Harness();
        soloOut.BeginAndSpawnBoss(duo: false);
        var so0 = soloOut.CrumbleIntoP3(atTick: 200);
        soloOut.Engine.OnMidpointBlast(soloOut.Boss!.Position + new WorldVector(3.6d, 0d), so0 + 1, PairTier.None, 0d);
        Assert.False(soloOut.Engine.BurstWindowOpen);
    }

    [Fact]
    public void WardBreak_DuoMode_RequiresPairedTierAndMinimumSeparation()
    {
        // EXPLOIT 1 (Fable design-grill CRITICAL-1): a stacked pair — both players shoved the same radial direction by
        // a knockback pulse, so their midpoint barely moved — still earns a Good/Perfect confirm (tier requirement met)
        // but the pair separation at resolve is tiny. Must NOT break the ward: satisfying the tier alone isn't enough.
        var stacked = new Harness();
        stacked.BeginAndSpawnBoss(duo: true);
        var st0 = stacked.CrumbleIntoP3(atTick: 200);
        stacked.Engine.OnMidpointBlast(stacked.Boss!.Position, st0 + 1, PairTier.Good, pairSeparationUnits: 0.4d);
        Assert.False(stacked.Engine.BurstWindowOpen);

        // EXPLOIT 2: one player presses V and lets the 1.5 s confirm window lapse — the degraded solo self-blast
        // (PairTier.None) breaks the ward alone even at a generous separation value. Must NOT break it in duo mode:
        // a solo self-blast is never a substitute for the duo contest.
        var soloSelf = new Harness();
        soloSelf.BeginAndSpawnBoss(duo: true);
        var so0 = soloSelf.CrumbleIntoP3(atTick: 200);
        soloSelf.Engine.OnMidpointBlast(soloSelf.Boss!.Position, so0 + 1, PairTier.None, pairSeparationUnits: 99d);
        Assert.False(soloSelf.Engine.BurstWindowOpen);

        // The intended play: a confirmed Good/Perfect blast landed with the pair spread >= MinPairSeparationUnits
        // apart at resolve DOES break the ward.
        var separated = new Harness();
        separated.BeginAndSpawnBoss(duo: true);
        var se0 = separated.CrumbleIntoP3(atTick: 200);
        separated.Engine.OnMidpointBlast(separated.Boss!.Position, se0 + 1, PairTier.Good, BossEncounterEngine.MinPairSeparationUnits);
        Assert.True(separated.Engine.BurstWindowOpen);
    }

    // S-boss-p3-partner-loss-dead-run: c2c03dd's DUO-GRILL gate rejects the solo self-blast (PairTier.None) in duo
    // mode FOREVER — including for a survivor whose partner disconnected mid-P3, since the survivor can only ever
    // produce that solo self-blast alone. Fix: the gate now recomputes duo-vs-solo from the LIVE participant count
    // (not the spawn-fixed one) on every blast, so a run that drops to one live participant falls back to solo ward
    // rules (no tier/separation requirement, the wider 3.5u radius) — never a permanently-unreachable duo gate.
    [Fact]
    public void WardBreak_PartnerDisconnected_DowngradesToSoloRules_AndAnnouncesOnce()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var partner = players[1];
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // Partner disconnects: GameServer's OnPeerDisconnected runs BreakPair then Zone.Despawn — the entity is
        // fully gone, exactly like this direct World.Remove.
        Assert.True(h.World.Remove(partner.Id, out _));

        // Step once so StepP3 observes the downgrade this tick and fires the one-shot legibility chat — independent
        // of whether/when a blast is ever attempted.
        h.Engine.Step(t0 + 1);
        Assert.Contains(h.Announcements, a => a.Text.Contains("bond is broken"));
        var announceCountAfterFirstStep = h.Announcements.Count(a => a.Text.Contains("bond is broken"));

        // Stepping further does not repeat the announce (one-shot latch).
        h.Engine.Step(t0 + 2);
        Assert.Equal(announceCountAfterFirstStep, h.Announcements.Count(a => a.Text.Contains("bond is broken")));

        // The survivor's solo self-blast (PairTier.None, zero separation — the exact report shape that was REJECTED
        // forever pre-fix) now breaks the ward under solo rules: no tier/separation gate, and the wider 3.5u radius
        // (would have been rejected at 3.4u under the 2.5u duo radius too).
        h.Engine.OnMidpointBlast(h.Boss!.Position + new WorldVector(3.4d, 0d), t0 + 3, PairTier.None, pairSeparationUnits: 0d);
        Assert.True(h.Engine.BurstWindowOpen);
    }

    [Fact]
    public void WardBreak_PartnerDied_DowngradesToSoloRules_SameAsDisconnect()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var partner = players[1];
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // Partner dies: NOT despawned (no BreakPair for death) — kept in `_participants` as a Health<=0 corpse in
        // the arena until the town respawn. Still resolvable, just dead — the disconnect case's mirror image.
        Assert.True(partner.ApplyDamage(partner.Stats.Health));
        Assert.Equal(0, partner.Stats.Health);

        h.Engine.Step(t0 + 1);
        // Not a wipe: the issuer is still alive-in-arena (only ALL participants dead triggers the wipe reset).
        Assert.Equal(EncounterState.Active, h.Engine.State);
        Assert.Contains(h.Announcements, a => a.Text.Contains("bond is broken"));

        h.Engine.OnMidpointBlast(h.Boss!.Position + new WorldVector(3.4d, 0d), t0 + 2, PairTier.None, pairSeparationUnits: 0d);
        Assert.True(h.Engine.BurstWindowOpen);
    }

    // A LIVE duo (both participants alive, spawned duo) is UNAFFECTED — the existing DUO-GRILL exploit gate
    // (WardBreak_DuoMode_RequiresPairedTierAndMinimumSeparation) still rejects a stacked pair / a lapsed solo self-
    // blast when BOTH participants are live; this just re-confirms the live-count recompute doesn't loosen anything
    // while the partner is present.
    [Fact]
    public void WardBreak_BothPartnersLive_DuoGateStillApplies()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // The solo self-blast shape, with both partners still alive → still rejected (the exploit the DUO-GRILL
        // gate exists to close).
        h.Engine.OnMidpointBlast(h.Boss!.Position, t0 + 1, PairTier.None, pairSeparationUnits: 99d);
        Assert.False(h.Engine.BurstWindowOpen);
    }

    [Fact]
    public void WardBreak_BroadcastsPlating_OnBurstAndReform()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        var t0 = h.CrumbleIntoP3(atTick: 200);

        h.Engine.OnMidpointBlast(h.Boss!.Position, t0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.True(h.PlatingBroadcasts[^1].BossId == bossId && !h.PlatingBroadcasts[^1].Active); // burst → tint OFF.
        Assert.Contains(h.Announcements, a => a.Text.Contains("EXPOSED"));

        h.StepThrough(t0 + 2, t0 + 161);
        Assert.True(h.PlatingBroadcasts[^1].BossId == bossId && h.PlatingBroadcasts[^1].Active); // reform → tint ON.
    }

    [Fact]
    public void Blast_DuringP1P2OrIdle_IsIgnored()
    {
        // Idle: never began. A qualifying duo-tier/separation report is used throughout this test so the phase gate
        // (not the DUO-GRILL tier/separation gate) is the only variable under test.
        var idle = new Harness();
        idle.Engine.OnMidpointBlast(WorldVector.Zero, 0, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.False(idle.Engine.BurstWindowOpen);

        // P1 (above 70%): a blast on the boss does nothing (fusion is the P1 gate; the detonation is inert until P3).
        var p1 = new Harness();
        p1.BeginAndSpawnBoss(duo: true);
        p1.Engine.OnMidpointBlast(p1.Boss!.Position, 61, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.False(p1.Engine.BurstWindowOpen);

        // P2 (40–70%): still no ward to break — the detonation reports are ignored until P3.
        var p2 = new Harness();
        p2.BeginAndSpawnBoss(duo: true);
        var t0 = p2.CrumbleIntoP2(atTick: 100);
        p2.Engine.OnMidpointBlast(p2.Boss!.Position, t0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.False(p2.Engine.BurstWindowOpen);
    }

    [Fact]
    public void SweepBeam_AdvancesBearingConsistently_OnCadence()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // First beam 4 s (80 t) after the root; none before.
        h.StepThrough(t0 + 1, t0 + 79);
        Assert.Empty(h.Beams);
        h.Engine.Step(t0 + 80);
        Assert.Single(h.Beams);
        var first = h.Beams[0];
        Assert.Equal(t0 + 80u, first.Start);
        Assert.Equal(t0 + 104u, first.Resolve); // 1.2 s windup = 24 t.
        Assert.Equal(25, first.Damage);
        Assert.Equal(16d, first.Length); // review MEDIUM-2: 16u covers the farthest corner from the true-centre root.
        Assert.Equal(1d, first.HalfWidth);

        // Next beams every 3 s (60 t), each advancing the bearing a consistent ~40°.
        h.StepThrough(t0 + 81, t0 + 260);
        Assert.True(h.Beams.Count >= 3);
        var step = 40d * System.Math.PI / 180d;
        for (var i = 1; i < h.Beams.Count; i++)
        {
            Assert.Equal(60u, h.Beams[i].Start - h.Beams[i - 1].Start); // base cadence.
            var delta = NormalizePi(h.Beams[i].Aim - h.Beams[i - 1].Aim);
            Assert.Equal(step, System.Math.Abs(delta), 3);
        }
    }

    [Fact]
    public void KnockbackPulse_ShovesAllLivingParticipants_RadiallyAwayFromBoss()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 200);
        var boss = h.Boss!;

        // First pulse cue 6 s (120 t) after the root; the shove lands 1 s (20 t) later at +140. Nothing before.
        h.StepThrough(t0 + 1, t0 + 139);
        Assert.Empty(h.Displacements);
        Assert.Contains(h.Announcements, a => a.Text.Contains("brace"));

        // Capture each participant's pre-shove distance from the boss, then step the shove tick.
        var preDist = players.ToDictionary(p => p.Id, p => (p.Position - boss.Position).Length);
        h.Engine.Step(t0 + 140);
        Assert.Equal(2, h.Displacements.Count); // both living participants shoved.
        foreach (var player in players)
        {
            var shove = h.Displacements.First(d => d.Id == player.Id);
            // Shoved 3u radially AWAY: the target sits exactly PulseShoveUnits farther from the boss than before.
            Assert.Equal(preDist[player.Id] + 3d, (shove.To - boss.Position).Length, 3);
        }
    }

    [Fact]
    public void Enrage_BelowTenPercent_SpeedsTheBeam_TricklesSplinters_AndAnnouncesOnce()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 200);

        // Drop to exactly 10% (120/1200) so the enrage edge fires on the next Step.
        h.Boss!.ApplyDamage(h.Boss!.Stats.Health - 120);
        h.Engine.Step(t0 + 1);
        Assert.True(h.Engine.Enraged);
        Assert.Contains(h.Announcements, a => a.Text.Contains("rages"));

        // Beams now fire on the enraged cadence (3 s / 1.3 ≈ 47 t) — faster than the 60 t base. Step to just before the
        // first splinter trickle (which lands 10 s = 200 t after the enrage edge at t0+1 → t0+201).
        h.StepThrough(t0 + 2, t0 + 200);
        Assert.True(h.Beams.Count >= 2);
        Assert.Equal(47u, h.Beams[1].Start - h.Beams[0].Start);
        Assert.Equal(0, h.SplinterSpawnCount); // no trickle yet.

        // The trickle: one splinter at t0+201, tracked in the shared add ledger.
        h.Engine.Step(t0 + 201);
        Assert.Equal(1, h.SplinterSpawnCount);
        Assert.Equal(1, h.Engine.AddCount);
    }

    [Fact]
    public void P3Activity_StopsOnEveryEndPath()
    {
        // WIPE: both die in-arena → immediate reset; no P3 activity after.
        var wipe = new Harness();
        var wp = wipe.BeginAndSpawnBoss(duo: true);
        var w0 = wipe.CrumbleIntoP3(atTick: 200);
        wp[0].ApplyDamage(wp[0].Stats.Health);
        wp[1].ApplyDamage(wp[1].Stats.Health);
        wipe.Engine.Step(w0 + 1);
        Assert.Equal(EncounterState.Idle, wipe.Engine.State);
        Assert.False(wipe.Engine.P3Active);
        var beamsAfterWipe = wipe.Beams.Count;
        wipe.StepThrough(w0 + 2, w0 + 300);
        Assert.Equal(beamsAfterWipe, wipe.Beams.Count); // no new beams after the reset.

        // LEAVE → empty → grace reset stops P3.
        var leave = new Harness();
        var lp = leave.BeginAndSpawnBoss(duo: false);
        var l0 = leave.CrumbleIntoP3(atTick: 200);
        Assert.True(leave.Engine.TryLeave(lp[0], out _));
        leave.StepThrough(l0 + 1, l0 + 1 + 200); // arm + 10 s grace.
        Assert.Equal(EncounterState.Idle, leave.Engine.State);
        Assert.False(leave.Engine.P3Active);

        // VICTORY (boss killed) → back to Idle, P3 stops, victors retained.
        var victory = new Harness();
        victory.BeginAndSpawnBoss(duo: true);
        var v0 = victory.CrumbleIntoP3(atTick: 200);
        Assert.True(victory.World.Remove(victory.Engine.BossId, out _));
        victory.Engine.Step(v0 + 1);
        Assert.Equal(EncounterState.Idle, victory.Engine.State);
        Assert.False(victory.Engine.P3Active);
        Assert.Contains(victory.Announcements, a => a.Text.Contains("Victory"));
        Assert.Equal(2, victory.Engine.ParticipantCount); // victors retained.
    }

    [Fact]
    public void VictoryDuringBurstWindow_StillWins()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 200);
        // Break the ward, then kill the boss WHILE the burst window is open (the intended kill path).
        h.Engine.OnMidpointBlast(h.Boss!.Position, t0 + 1, PairTier.Perfect, BossEncounterEngine.MinPairSeparationUnits);
        Assert.True(h.Engine.BurstWindowOpen);
        Assert.True(h.World.Remove(h.Engine.BossId, out _));
        h.Engine.Step(t0 + 2);
        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Contains(h.Announcements, a => a.Text.Contains("Victory"));
        Assert.False(h.Engine.P3Active);
    }

    [Fact]
    public void BeamResolve_AndPulseShove_NeverShareATick_ByConstruction()
    {
        var h = new Harness();
        var players = h.BeginAndSpawnBoss(duo: true);
        var t0 = h.CrumbleIntoP3(atTick: 137); // an arbitrary (odd) anchor — disjointness must hold for ANY anchor.
        foreach (var player in players)
        {
            player.SetMaxHealthFull(10_000_000); // tanky; the boss stays at 35% so no enrage re-paces the beam.
        }

        var shoveTicks = new List<uint>();
        var shovesSeen = 0;
        for (var t = t0 + 1; t <= t0 + 1200; t++) // 60 s of P3.
        {
            // Re-centre both each tick so the repeated 3u shoves never push them out of the arena (which would empty it).
            h.MoveTo(players[0], ArenaCentre + new WorldVector(-1d, 0d));
            h.MoveTo(players[1], ArenaCentre + new WorldVector(1d, 0d));
            h.Engine.Step(t);
            if (h.Displacements.Count > shovesSeen)
            {
                shoveTicks.Add(t);
                shovesSeen = h.Displacements.Count;
            }
        }

        var beamResolveTicks = h.Beams.Select(b => b.Resolve).Distinct().ToHashSet();
        var shoveSet = shoveTicks.ToHashSet();
        Assert.NotEmpty(beamResolveTicks);
        Assert.NotEmpty(shoveSet);
        Assert.False(h.Engine.Enraged); // base cadence throughout (residues stay disjoint by construction).
        Assert.Empty(beamResolveTicks.Intersect(shoveSet));
    }

    // Principal-range angle difference (mirrors TelegraphShape's helper) for the beam-bearing advance assertion.
    private static double NormalizePi(double radians)
    {
        var twoPi = 2d * System.Math.PI;
        radians %= twoPi;
        if (radians <= -System.Math.PI)
        {
            radians += twoPi;
        }
        else if (radians > System.Math.PI)
        {
            radians -= twoPi;
        }

        return radians;
    }
}
