using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// TELEGRAPH T1 (docs/ability-telegraph-sync-design.md): the server-side scheduled-telegraph engine — the DEADLINE
// half of the telegraph model. Schedule() stamps a pending telegraph with an ABSOLUTE resolve tick T (= cast tick +
// windup); ResolveDue(), run once per server tick from TickCore, resolves every telegraph whose T arrived: gather
// spatial candidates around the LOCKED origin (a superset box query — the AOI gather pattern), exact-test each
// against the shape at its CURRENT position, and damage the alive PLAYERS inside through the injected choke point
// (PlayerDamageGate.TryDamagePlayer). Membership is judged at tick T, never at cast — that is precisely what makes a
// telegraph dodgeable (design: "the server checks where you are when it resolves, not where you were when it
// started"). Casters/monsters are unaffected this phase (no friendly fire, mirroring ApplyMonsterAttack's targeting).
//
// CASTER LIFETIME (decided + pinned by TelegraphSchedulerTests): a telegraph OUTLIVES its caster. Once cast, the
// wound-up danger exists in the world — a monster killed mid-windup does not defuse the slam already coming down
// (and resolve never dereferences the caster, so a despawn cannot dangle). CasterId is carried for attribution +
// the T2 wire event only.
//
// WIRE (T2): the per-recipient AOI diff pass in GameServer replicates each pending telegraph as a TelegraphMessage
// {id, shape, startTick, resolveTick} via CopyActiveTo/IsPending below — clients render the fill against the shared
// deadline and self-resolve at T, so this engine still never sends anything itself and NO resolve/cancel message
// exists. Server-tick uint arithmetic throughout (a plain >= — the tick counter would take ~6.8 years @ 20 Hz to
// wrap).
public sealed class TelegraphScheduler
{
    // The spatial superset gather (WorldState.GatherInterestCandidates) — the SAME index AOI/combat/aggro use, so
    // telegraph occupancy can never diverge from replication. Injected so the engine is testable against a bare
    // WorldState.
    public delegate void GatherCandidatesDelegate(TileCoord center, int radiusTiles, List<WorldEntity> destination);

    // The player-damage CHOKE POINT (PlayerDamageGate.TryDamagePlayer) — dead-guard + i-frame gate + ApplyDamage +
    // the landed tail live INSIDE it, never here. Injected as the method group so this engine and the monster melee
    // provably share one gate.
    public delegate bool TryDamagePlayerDelegate(WorldEntity victim, int amount, uint serverTick, string source);

    // A scheduled, not-yet-resolved telegraph. The shape (with its LOCKED cast-time origin) and the damage are
    // captured at schedule time; StartTick is the tick it was scheduled on (T2: rides the wire so every viewer —
    // including a late AOI joiner — computes the SAME fill fraction (now − start)/(T − start), not one restarted at
    // its own receive time); ResolveTick is the absolute deadline. Source is the attribution string the damage
    // log/display carries ("Slime slam", "/slam by Admin").
    private readonly record struct PendingTelegraph(
        ulong Id, ulong CasterId, TelegraphShape Shape, uint StartTick, uint ResolveTick, int Damage, string Source);

    // TELEGRAPH T2: the read-only projection of a pending telegraph the wire sync needs — exactly the TelegraphMessage
    // payload (id + locked shape + the two absolute ticks), no damage/source/caster (those never replicate; damage is
    // resolved server-side and attribution rides the damage log).
    public readonly record struct ActiveTelegraph(ulong Id, TelegraphShape Shape, uint StartTick, uint ResolveTick);

    private readonly GatherCandidatesDelegate _gatherCandidates;
    private readonly TryDamagePlayerDelegate _tryDamagePlayer;
    private readonly List<PendingTelegraph> _pending = [];

    // Reused scratch: the telegraphs due THIS tick (collected + removed from _pending BEFORE any damage runs, so a
    // damage callback that schedules a new telegraph — a future chained ability — can never perturb the resolve
    // iteration), and the spatial candidates per resolve. Single-threaded tick loop, so reuse is safe.
    private readonly List<PendingTelegraph> _dueScratch = [];
    private readonly List<WorldEntity> _candidateScratch = [];

    // Monotonic telegraph id — server-local this phase; T2 keys the wire event + its client rendering off it.
    private ulong _nextTelegraphId = 1;

    public TelegraphScheduler(GatherCandidatesDelegate gatherCandidates, TryDamagePlayerDelegate tryDamagePlayer)
    {
        _gatherCandidates = gatherCandidates ?? throw new ArgumentNullException(nameof(gatherCandidates));
        _tryDamagePlayer = tryDamagePlayer ?? throw new ArgumentNullException(nameof(tryDamagePlayer));
    }

    // The not-yet-resolved count — the leak sentinel the tests assert on (a resolved telegraph must always leave).
    public int PendingCount => _pending.Count;

    // Schedule a telegraph: `shape` (origin LOCKED at the caller's cast-time choice) scheduled AT `startTick` (the
    // caller's current server tick — T2 replicates it so every viewer computes the same fill fraction) and resolving
    // at the absolute `resolveTick`, dealing `damage` to every alive player inside it AT that tick. Returns the
    // telegraph id.
    public ulong Schedule(ulong casterId, TelegraphShape shape, uint startTick, uint resolveTick, int damage, string source)
    {
        var id = _nextTelegraphId++;
        _pending.Add(new PendingTelegraph(id, casterId, shape, startTick, resolveTick, damage, source));
        return id;
    }

    // TELEGRAPH T2: copy the wire projection of every still-pending telegraph into `destination` (cleared first; the
    // caller reuses its scratch — the set is tiny, telegraphs live ~1.5 s). The per-recipient AOI diff pass iterates
    // this to send TelegraphMessage to viewers that don't yet know an id (schedule-time send and mid-windup AOI-enter
    // are the SAME diff — the SpawnerMarker pattern).
    public void CopyActiveTo(List<ActiveTelegraph> destination)
    {
        destination.Clear();
        foreach (var telegraph in _pending)
        {
            destination.Add(new ActiveTelegraph(telegraph.Id, telegraph.Shape, telegraph.StartTick, telegraph.ResolveTick));
        }
    }

    // TELEGRAPH T2: whether `id` is still pending — the wire sync's forget test (a session's known-telegraph id is
    // dropped once its telegraph resolved, so the known set can never grow past the live set). Linear scan on purpose:
    // _pending is a handful of entries at most, so a lookup structure would cost more than it saves.
    public bool IsPending(ulong id)
    {
        foreach (var telegraph in _pending)
        {
            if (telegraph.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    // Resolve every telegraph whose resolve tick has arrived (>= is belt-and-braces; the tick loop calls this every
    // tick, so a telegraph resolves on EXACTLY its tick T). Due entries are compacted out of _pending FIRST (stable
    // schedule order preserved, no per-tick alloc), then resolved — see the reentrancy note on _dueScratch.
    public void ResolveDue(uint serverTick)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        _dueScratch.Clear();
        var write = 0;
        for (var read = 0; read < _pending.Count; read++)
        {
            var telegraph = _pending[read];
            if (serverTick >= telegraph.ResolveTick)
            {
                _dueScratch.Add(telegraph);
            }
            else
            {
                _pending[write++] = telegraph;
            }
        }

        if (_dueScratch.Count == 0)
        {
            return;
        }

        _pending.RemoveRange(write, _pending.Count - write);
        foreach (var telegraph in _dueScratch)
        {
            Resolve(telegraph, serverTick);
        }
    }

    // Resolve ONE due telegraph: superset-gather the entities around the locked origin (⌈bounding radius⌉ + 1 tiles
    // — the same strict-superset margin the aggro scan uses, so a fractional position at the box edge is never
    // dropped), then exact-test each candidate's CURRENT position against the shape. Players only, alive only
    // (mirroring ApplyMonsterAttack's targeting — a downed 0-HP body is not re-hit); the choke point re-guards
    // dead/i-framed victims authoritatively.
    private void Resolve(in PendingTelegraph telegraph, uint serverTick)
    {
        var gatherRadius = Math.Max(1, (int)Math.Ceiling(telegraph.Shape.BoundingRadius) + 1);
        _gatherCandidates(telegraph.Shape.Origin.ToTileRounded(), gatherRadius, _candidateScratch);
        foreach (var candidate in _candidateScratch)
        {
            if (candidate.Kind != EntityKind.Player || candidate.Stats.Health <= 0)
            {
                continue;
            }

            if (!telegraph.Shape.Contains(candidate.Position))
            {
                continue;
            }

            _tryDamagePlayer(candidate, telegraph.Damage, serverTick, telegraph.Source);
        }
    }
}
