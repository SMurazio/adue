using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// DUO-WAVE2 ability 4 (Midpoint Detonation): the server-side stepper for the initiate->confirm->charge->blast co-op
// nuke. Modelled on TelegraphScheduler but with a LIVE-TRACKING origin — the telegraph's locked-origin resolve resists
// a moving origin cleanly, so this is a small parallel stepper that reuses the telegraph CIRCLE shape + center-point
// membership rule and drives a dedicated MidpointCharge client decal (see the codec/message note). Flow:
//   1. INITIATE — a player presses V; a pending op is recorded, and an echo cue flashes on the partner.
//   2. CONFIRM — the partner presses V within ConfirmWindowTicks; the tier is set by the confirm timing (Perfect/Good),
//      an echo cue flashes on the initiator, and the charge begins.
//   3. CHARGE — for ChargeTicks the blast marker LIVE-updates to the midpoint between the two players (they aim it by
//      repositioning); a MidpointCharge message rides to both each tick.
//   4. RESOLVE — at charge end the blast resolves at the FINAL midpoint (center-point membership, the telegraph rule),
//      damaging MONSTERS ONLY (the melee seam), and leaves a lingering slow zone.
//   * DEGRADATION — if no confirm arrives within ConfirmWindowTicks, the initiator gets a small solo blast on THEMSELVES.
public sealed class MidpointDetonationEngine
{
    // ---- tunables (the one obvious place; experiment values from the orchestrator spec) ----
    public const uint ConfirmWindowTicks = 30;   // 1.5s @20Hz — the partner must confirm within this
    public const uint PerfectConfirmTicks = 6;   // confirm within this -> Perfect tier
    public const uint GoodConfirmTicks = 30;     // confirm within this (but slower than Perfect) -> Good tier
    public const uint ChargeTicks = 16;          // 0.8s @20Hz — charge AFTER confirm

    public const double PerfectRadiusUnits = 3.5d;
    public const int PerfectDamage = 30;
    public const double GoodRadiusUnits = 2.5d;
    public const int GoodDamage = 20;
    public const double SoloRadiusUnits = 1.5d;  // graceful degradation blast on the initiator
    public const int SoloDamage = 10;

    public const uint SlowZoneDurationTicks = 40; // 2s lingering slow zone after the blast

    // ---- injected seams (fakes in tests) ----

    public delegate void GatherCandidatesDelegate(TileCoord center, int radiusTiles, List<WorldEntity> destination);

    // Damage `monster` (attributed to `attributedTo`) through the SAME melee seam the skillshot/tether use.
    public delegate void DamageMonsterDelegate(WorldEntity monster, WorldEntity attributedTo, int amount, uint serverTick);

    // Briefly slow `monster` — the SHARED monster-slow seam the tether uses (the lingering slow zone re-arms it each
    // tick a monster stands in the zone).
    public delegate void SlowMonsterDelegate(WorldEntity monster, uint serverTick);

    // Flash the brief echo cue on `target` (initiate cue on the partner, confirm cue on the initiator).
    public delegate void EchoCueDelegate(WorldEntity target, EchoCueKind cue);

    // Replicate a charge marker to BOTH players (MidpointCharge). Active=false is the end edge (drop the decal). Sent
    // each charge tick with the LIVE origin so the decal tracks the moving midpoint.
    public delegate void ChargeUpdateDelegate(
        WorldEntity initiator, WorldEntity partner, ulong chargeId, WorldVector origin, double radiusUnits,
        uint startTick, uint resolveTick, bool active);

    private enum Phase { Pending, Charging }

    private sealed class Detonation
    {
        public ulong Id;
        public Phase Phase;
        public WorldEntity Initiator = null!;
        public WorldEntity Confirmer = null!; // null-ref until confirmed (solo path never reads it)
        public bool HasPartner;               // did the initiator have a partner at initiate time?
        public WorldEntity Partner = null!;   // the partner at initiate time (for the initiate echo cue / charge send)
        public uint InitiateTick;
        public uint ConfirmTick;
        public uint ChargeEndTick;
        public PairTier Tier;
    }

    private readonly record struct SlowZone(WorldVector Center, double RadiusUnits, uint ExpiryTick, ulong AttributedToId);

    private readonly GatherCandidatesDelegate _gather;
    private readonly DamageMonsterDelegate _damageMonster;
    private readonly SlowMonsterDelegate _slowMonster;
    private readonly EchoCueDelegate _echoCue;
    private readonly ChargeUpdateDelegate _chargeUpdate;

    private readonly List<Detonation> _detonations = [];
    private readonly List<SlowZone> _slowZones = [];
    private readonly List<WorldEntity> _candidateScratch = [];
    private ulong _nextChargeId = 1;

    public MidpointDetonationEngine(
        GatherCandidatesDelegate gather,
        DamageMonsterDelegate damageMonster,
        SlowMonsterDelegate slowMonster,
        EchoCueDelegate echoCue,
        ChargeUpdateDelegate chargeUpdate)
    {
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _damageMonster = damageMonster ?? throw new ArgumentNullException(nameof(damageMonster));
        _slowMonster = slowMonster ?? throw new ArgumentNullException(nameof(slowMonster));
        _echoCue = echoCue ?? throw new ArgumentNullException(nameof(echoCue));
        _chargeUpdate = chargeUpdate ?? throw new ArgumentNullException(nameof(chargeUpdate));
    }

    public int PendingCount => _detonations.Count;
    public int SlowZoneCount => _slowZones.Count;

    // A player pressed V. If their partner has a PENDING op the presser can CONFIRM (the partner initiated), confirm
    // it (sets the tier + starts the charge). Otherwise start a fresh pending op initiated by the presser (an echo
    // cue flashes the partner). `partner` may be null (solo player) — a solo initiate still degrades to a self-blast.
    public void PressDetonate(WorldEntity presser, WorldEntity? partner, uint serverTick)
    {
        // Confirm: an op initiated BY the partner, still pending, that this presser can complete.
        if (partner is not null)
        {
            foreach (var op in _detonations)
            {
                if (op.Phase == Phase.Pending && op.Initiator.Id == partner.Id && op.HasPartner && op.Partner.Id == presser.Id)
                {
                    Confirm(op, presser, serverTick);
                    return;
                }
            }
        }

        // Already have a pending op initiated by this presser — ignore a double press (no re-arm).
        foreach (var op in _detonations)
        {
            if (op.Initiator.Id == presser.Id)
            {
                return;
            }
        }

        // Fresh initiate.
        _detonations.Add(new Detonation
        {
            Id = _nextChargeId++,
            Phase = Phase.Pending,
            Initiator = presser,
            HasPartner = partner is not null,
            Partner = partner!,
            InitiateTick = serverTick,
        });

        if (partner is not null)
        {
            _echoCue(partner, EchoCueKind.DetonateInitiate);
        }
    }

    private void Confirm(Detonation op, WorldEntity confirmer, uint serverTick)
    {
        op.Phase = Phase.Charging;
        op.Confirmer = confirmer;
        op.ConfirmTick = serverTick;
        op.ChargeEndTick = serverTick + ChargeTicks;
        op.Tier = PairedTimingWindow.Classify(serverTick, op.InitiateTick, PerfectConfirmTicks, GoodConfirmTicks);
        // Classify never returns None here (delta <= ConfirmWindowTicks == GoodConfirmTicks was checked by the step
        // loop expiring the op), but guard: a delta past the good window degrades to Good rather than None.
        if (op.Tier == PairTier.None)
        {
            op.Tier = PairTier.Good;
        }

        _echoCue(op.Initiator, EchoCueKind.DetonateConfirm);
    }

    // One tick: expire un-confirmed ops into a solo blast, live-update + resolve charging ops, and tick lingering slow
    // zones. ~free when nothing is pending.
    public void Step(uint serverTick)
    {
        for (var index = _detonations.Count - 1; index >= 0; index--)
        {
            var op = _detonations[index];
            if (op.Phase == Phase.Pending)
            {
                if (serverTick - op.InitiateTick >= ConfirmWindowTicks)
                {
                    // DEGRADATION: no confirm in the window — a small blast centred on the initiator.
                    ResolveBlast(op.Initiator.Position, SoloRadiusUnits, SoloDamage, op.Initiator, serverTick);
                    _detonations.RemoveAt(index);
                }

                continue;
            }

            // Charging: live-track the marker to the current midpoint, resolve at charge end.
            var midpoint = Midpoint(op.Initiator, op.Confirmer);
            var (radius, damage) = op.Tier == PairTier.Perfect ? (PerfectRadiusUnits, PerfectDamage) : (GoodRadiusUnits, GoodDamage);
            if (serverTick >= op.ChargeEndTick)
            {
                _chargeUpdate(op.Initiator, op.Confirmer, op.Id, midpoint, radius, op.ConfirmTick, op.ChargeEndTick, false);
                ResolveBlast(midpoint, radius, damage, op.Initiator, serverTick);
                _detonations.RemoveAt(index);
            }
            else
            {
                _chargeUpdate(op.Initiator, op.Confirmer, op.Id, midpoint, radius, op.ConfirmTick, op.ChargeEndTick, true);
            }
        }

        StepSlowZones(serverTick);
    }

    // Resolve a blast: damage every attackable monster whose CENTRE is inside the circle (the telegraph center-point
    // rule), and leave a lingering slow zone at the same centre.
    private void ResolveBlast(WorldVector center, double radiusUnits, int damage, WorldEntity attributedTo, uint serverTick)
    {
        var shape = TelegraphShape.Circle(center, radiusUnits);
        var gatherRadius = System.Math.Max(1, (int)System.Math.Ceiling(shape.BoundingRadius) + 1);
        _gather(center.ToTileRounded(), gatherRadius, _candidateScratch);
        foreach (var candidate in _candidateScratch)
        {
            if (candidate.Kind != EntityKind.Monster || !CombatTargeting.IsAttackableEnemy(candidate) || candidate.Stats.Health <= 0)
            {
                continue;
            }

            if (shape.Contains(candidate.Position))
            {
                _damageMonster(candidate, attributedTo, damage, serverTick);
            }
        }

        _slowZones.Add(new SlowZone(center, radiusUnits, serverTick + SlowZoneDurationTicks, attributedTo.Id));
    }

    // Each lingering slow zone: while alive, slow every attackable monster whose centre is inside it (re-arming the
    // monster's brief slow each tick it stands in the zone). Expire zones whose deadline passed.
    private void StepSlowZones(uint serverTick)
    {
        for (var index = _slowZones.Count - 1; index >= 0; index--)
        {
            var zone = _slowZones[index];
            if (serverTick >= zone.ExpiryTick)
            {
                _slowZones.RemoveAt(index);
                continue;
            }

            var shape = TelegraphShape.Circle(zone.Center, zone.RadiusUnits);
            var gatherRadius = System.Math.Max(1, (int)System.Math.Ceiling(shape.BoundingRadius) + 1);
            _gather(zone.Center.ToTileRounded(), gatherRadius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                if (candidate.Kind != EntityKind.Monster || !CombatTargeting.IsAttackableEnemy(candidate) || candidate.Stats.Health <= 0)
                {
                    continue;
                }

                if (shape.Contains(candidate.Position))
                {
                    _slowMonster(candidate, serverTick);
                }
            }
        }
    }

    // Tear down any pending/charging op involving `entityId` (unpair / disconnect / death). Does not resolve a blast —
    // a broken pair simply cancels the in-progress detonation. Lingering slow zones already spawned are left to expire.
    public void RemoveInvolving(ulong entityId)
    {
        for (var index = _detonations.Count - 1; index >= 0; index--)
        {
            var op = _detonations[index];
            if (op.Initiator.Id == entityId || (op.HasPartner && op.Partner.Id == entityId)
                || (op.Phase == Phase.Charging && op.Confirmer.Id == entityId))
            {
                if (op.Phase == Phase.Charging)
                {
                    _chargeUpdate(op.Initiator, op.Confirmer, op.Id, Midpoint(op.Initiator, op.Confirmer), 0d, op.ConfirmTick, op.ChargeEndTick, false);
                }

                _detonations.RemoveAt(index);
            }
        }
    }

    private static WorldVector Midpoint(WorldEntity a, WorldEntity b) => (a.Position + b.Position) * 0.5d;
}
