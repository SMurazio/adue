using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// DUO-WAVE2 ability 3 (Laser Tether): the server-side stepper for the continuous beam that links two paired players
// while toggled on. Modelled on SkillshotEngine/TelegraphScheduler — injected seams, single-threaded tick loop, reused
// scratch, ~free when no tether is active. It OWNS the active tethers' state machine; the pure band/damage geometry
// lives in TetherMath (unit-tested directly). Each Step(), for every active tether:
//   * measure the beam length (distance between the two players' live positions),
//   * classify the band (TetherMath.Band),
//   * SWEET → every DamageIntervalTicks, damage + briefly slow every monster the beam SEGMENT crosses (the melee
//     seam + the monster-slow seam), damage scaling toward the band middle,
//   * OVERSTRETCH → tick a DoT to BOTH players THROUGH the player-damage choke point, and after OverstretchBreakTicks
//     continuously overstretched, BREAK the beam (a re-link cooldown then gates a fresh toggle),
//   * INERT / WARNING → nothing (and any overstretch timer resets — the break requires CONTINUOUS overstretch).
public sealed class TetherEngine
{
    // ---- tunables (the one obvious place; experiment values from the orchestrator spec) ----
    public const double InertMaxUnits = 3d;         // below this the beam is inert
    public const double SweetMaxUnits = 10d;        // [InertMax, SweetMax] is the damaging sweet spot
    public const double SweetMidUnits = 6.5d;       // damage peaks here (middle of the sweet band)
    public const double OverstretchMinUnits = 12d;  // >= this the beam over-tensions; (SweetMax, this) is the warning gap
    public const int MinTickDamage = 2;             // sweet-band per-tick damage at the band edges
    public const int MaxTickDamage = 5;             // sweet-band per-tick damage at the band middle
    public const uint DamageIntervalTicks = 5;      // apply the sweet-band monster damage every N ticks

    // The beam↔monster overlap radius (segment-vs-body): the SAME canonical body radius the melee/skillshot resolve
    // against, so the beam clips a monster it grazes — the orbit-sweep feel.
    public const double BeamHitRadiusUnits = FreeAimSector.EntityHitRadiusTiles;

    // Overstretch: 2 dmg/s to BOTH players = 1 damage every 10 ticks (@20Hz), applied through the gate. Break after 2s
    // (40 ticks) continuously overstretched; 3s (60 ticks) re-link cooldown before a fresh toggle is allowed.
    public const uint PlayerDotIntervalTicks = 10;
    public const int PlayerDotPerInterval = 1;
    public const uint OverstretchBreakTicks = 40;
    public const uint RelinkCooldownTicks = 60;

    // ---- injected seams (fakes in tests) ----

    // Gather candidate entities within `radiusTiles` of `center` (the SAME spatial index AOI/combat use) into
    // `destination` (cleared first) — the engine filters to attackable monsters and applies the segment overlap test.
    public delegate void GatherCandidatesDelegate(TileCoord center, int radiusTiles, List<WorldEntity> destination);

    // Apply `amount` beam damage to `monster` (attributed to `attributedTo` for the loot/contribution ledger) through
    // the SAME melee seam the skillshot uses. Return value is unused here (the beam never pierces on kill).
    public delegate void DamageMonsterDelegate(WorldEntity monster, WorldEntity attributedTo, int amount, uint serverTick);

    // Briefly slow `monster` (the tether's 30%/1s slow) — the shared monster-slow seam (also reused by ability 4's
    // lingering slow zone). Idempotent-ish: re-arming refreshes the slow's expiry.
    public delegate void SlowMonsterDelegate(WorldEntity monster, uint serverTick);

    // The player-damage CHOKE POINT (PlayerDamageGate.TryDamagePlayer) — the overstretch DoT to both players routes
    // through it like every other player-damage source (never bypassing the i-frame/shield/dead gate).
    public delegate bool TryDamagePlayerDelegate(WorldEntity victim, int amount, uint serverTick, string source);

    // Replicate a tether's on/off/broken state to BOTH partners (TetherStatusMessage). Called on toggle + on break.
    public delegate void StatusChangedDelegate(WorldEntity a, WorldEntity b, TetherState state);

    private sealed class Tether
    {
        public ulong AId;
        public ulong BId;
        public WorldEntity A = null!;
        public WorldEntity B = null!;
        // Null = not currently overstretched. NULLABLE, not a 0-sentinel: serverTick 0 is a legal tick (the headless
        // tests start there), and a 0-sentinel would silently re-seed the timer each tick while serverTick is 0 — the
        // break would fire one tick late and the player DoT double-apply on the first two ticks.
        public uint? OverstretchStartTick;
        public uint NextSweetDamageTick;
        public uint NextPlayerDotTick;
    }

    private readonly GatherCandidatesDelegate _gather;
    private readonly DamageMonsterDelegate _damageMonster;
    private readonly SlowMonsterDelegate _slowMonster;
    private readonly TryDamagePlayerDelegate _tryDamagePlayer;
    private readonly StatusChangedDelegate _onStatusChanged;

    private readonly List<Tether> _tethers = [];
    private readonly List<WorldEntity> _candidateScratch = [];
    // Per-pair re-link cooldown, keyed by the canonical (lower) entity id — persists after a BREAK so a fresh toggle
    // is gated for RelinkCooldownTicks even though the Tether object is gone.
    private readonly Dictionary<ulong, uint> _relinkReadyTick = [];

    public TetherEngine(
        GatherCandidatesDelegate gather,
        DamageMonsterDelegate damageMonster,
        SlowMonsterDelegate slowMonster,
        TryDamagePlayerDelegate tryDamagePlayer,
        StatusChangedDelegate onStatusChanged)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _damageMonster = damageMonster ?? throw new ArgumentNullException(nameof(damageMonster));
        _slowMonster = slowMonster ?? throw new ArgumentNullException(nameof(slowMonster));
        _tryDamagePlayer = tryDamagePlayer ?? throw new ArgumentNullException(nameof(tryDamagePlayer));
        _onStatusChanged = onStatusChanged ?? throw new ArgumentNullException(nameof(onStatusChanged));
    }

    public int ActiveCount => _tethers.Count;

    // Whether a tether currently links this pair (either order). The G key's toggle reads this to decide on/off.
    public bool IsActive(ulong entityIdA, ulong entityIdB)
    {
        foreach (var tether in _tethers)
        {
            if (LinksPair(tether, entityIdA, entityIdB))
            {
                return true;
            }
        }

        return false;
    }

    // Toggle the tether for a pair: if one is active, turn it OFF; else, unless the pair is inside its re-link
    // cooldown, turn it ON. Returns the resulting state (Off/On) and fires the status seam. A toggle-on inside the
    // re-link window is rejected (returns the current Off state, no status change) — the client already shows Off.
    public TetherState Toggle(WorldEntity a, WorldEntity b, uint serverTick)
    {
        for (var i = 0; i < _tethers.Count; i++)
        {
            if (LinksPair(_tethers[i], a.Id, b.Id))
            {
                _tethers.RemoveAt(i);
                _onStatusChanged(a, b, TetherState.Off);
                return TetherState.Off;
            }
        }

        var key = PairKey(a.Id, b.Id);
        if (_relinkReadyTick.TryGetValue(key, out var readyTick) && serverTick < readyTick)
        {
            return TetherState.Off;
        }

        _tethers.Add(new Tether
        {
            AId = a.Id,
            BId = b.Id,
            A = a,
            B = b,
            NextSweetDamageTick = serverTick,
            NextPlayerDotTick = serverTick,
        });
        _onStatusChanged(a, b, TetherState.On);
        return TetherState.On;
    }

    // Tear down any tether involving `entityId` (unpair / disconnect / death), pushing an Off status so both clients
    // drop the beam. Does NOT set a re-link cooldown (that is only the overstretch BREAK). Returns whether one was removed.
    public bool RemoveInvolving(ulong entityId)
    {
        for (var i = 0; i < _tethers.Count; i++)
        {
            var tether = _tethers[i];
            if (tether.AId == entityId || tether.BId == entityId)
            {
                _tethers.RemoveAt(i);
                _onStatusChanged(tether.A, tether.B, TetherState.Off);
                return true;
            }
        }

        return false;
    }

    // One tick: advance every active tether (band resolve + damage). ~free when none are active.
    public void Step(uint serverTick, double dtSeconds)
    {
        if (_tethers.Count == 0)
        {
            return;
        }

        // Iterate a stable index from the end so a mid-pass BREAK (removal) is safe.
        for (var index = _tethers.Count - 1; index >= 0; index--)
        {
            StepOne(_tethers[index], index, serverTick);
        }
    }

    private void StepOne(Tether tether, int index, uint serverTick)
    {
        var a = tether.A.Position;
        var b = tether.B.Position;
        var distance = (a - b).Length;
        var band = TetherMath.Band(distance, InertMaxUnits, SweetMaxUnits, OverstretchMinUnits);

        if (band != TetherBand.Overstretch)
        {
            // Leaving overstretch resets the break timer — the break requires CONTINUOUS overstretch.
            tether.OverstretchStartTick = null;
        }

        switch (band)
        {
            case TetherBand.Sweet:
                if (serverTick >= tether.NextSweetDamageTick)
                {
                    ResolveSweetDamage(tether, a, b, distance, serverTick);
                    tether.NextSweetDamageTick = serverTick + DamageIntervalTicks;
                }

                break;

            case TetherBand.Overstretch:
                if (!tether.OverstretchStartTick.HasValue)
                {
                    tether.OverstretchStartTick = serverTick;
                    tether.NextPlayerDotTick = serverTick;
                }

                if (serverTick >= tether.NextPlayerDotTick)
                {
                    _tryDamagePlayer(tether.A, PlayerDotPerInterval, serverTick, "Tether overstretch");
                    _tryDamagePlayer(tether.B, PlayerDotPerInterval, serverTick, "Tether overstretch");
                    tether.NextPlayerDotTick = serverTick + PlayerDotIntervalTicks;
                }

                if (serverTick - tether.OverstretchStartTick.Value >= OverstretchBreakTicks)
                {
                    _tethers.RemoveAt(index);
                    _relinkReadyTick[PairKey(tether.AId, tether.BId)] = serverTick + RelinkCooldownTicks;
                    _onStatusChanged(tether.A, tether.B, TetherState.Broken);
                }

                break;

            // Inert / Warning: no damage either way.
        }
    }

    // Damage + slow every attackable monster the beam segment [a,b] crosses this damage tick. Gathers over a tile box
    // that supersets the segment + body reach (mirrors the skillshot flight gather), then the exact segment-vs-body test.
    private void ResolveSweetDamage(Tether tether, WorldVector a, WorldVector b, double distance, uint serverTick)
    {
        var damage = TetherMath.SweetTickDamage(distance, InertMaxUnits, SweetMaxUnits, SweetMidUnits, MinTickDamage, MaxTickDamage);

        var midTile = ((a + b) * 0.5d).ToTileRounded();
        var gatherRadius = System.Math.Max(1, (int)System.Math.Ceiling((distance * 0.5d) + BeamHitRadiusUnits) + 1);
        _gather(midTile, gatherRadius, _candidateScratch);

        foreach (var candidate in _candidateScratch)
        {
            if (candidate.Kind != EntityKind.Monster || !CombatTargeting.IsAttackableEnemy(candidate) || candidate.Stats.Health <= 0)
            {
                continue;
            }

            if (!TetherMath.SegmentHitsBody(candidate.Position, a, b, BeamHitRadiusUnits))
            {
                continue;
            }

            _damageMonster(candidate, tether.A, damage, serverTick);
            _slowMonster(candidate, serverTick);
        }
    }

    private static bool LinksPair(Tether tether, ulong x, ulong y)
        => (tether.AId == x && tether.BId == y) || (tether.AId == y && tether.BId == x);

    // Canonical per-pair key for the re-link cooldown map: the lower of the two entity ids (a player is in at most one
    // pair, so either partner's id resolves to the same key).
    private static ulong PairKey(ulong x, ulong y) => x < y ? x : y;
}
