using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): the gnoll's brain — the FIRST genuinely per-type behavior, and
// the proof the P3 IMonsterBehavior seam earns its keep. A skirmisher behaves EXACTLY like the BasicRoamer (it inherits
// the whole Idle/Roaming/Chasing/Returning state machine + aggro scan + leash + attack + no-progress watchdog) EXCEPT
// that when WOUNDED it FLEES instead of pressing the attack: it overrides the ONE TryChooseFleeTarget hook the base
// class exposes and changes nothing else.
//
// FLEE = run directly AWAY from the chased target, recomputed each tick (so it keeps backpedaling as the target gives
// chase). It expresses this ONLY through the handed-in locomotion (the gnoll's GlideLocomotion is velocity-coherent —
// it SETS the replicated Velocity along the away-heading), so fleeing replicates + extrapolates smoothly on remote
// clients with NO protocol change (the replication guardrail, design §1.5). It is still subject to the inherited leash
// / de-aggro / watchdog: a fleer that outruns the de-aggro range, gets pulled past the chase leash, or wedges into a
// wall gives up and returns home exactly as a normal chaser would (the base StepChase runs those checks first).
//
// P4 SCOPE: this is the FLEE-WHEN-WOUNDED half of "skirmisher". The keep-distance / kiting half (hover at range, fire a
// ranged ability) is DEFERRED to P5, which gives the gnoll a ranged ability to kite with — there is nothing to kite
// with yet. Until then a healthy gnoll chases + melees like any BasicRoamer; only a wounded one differs.
public sealed class SkirmisherBehavior : BasicRoamerBehavior
{
    // Same ctor deps as BasicRoamerBehavior — passed straight through to the base (GameServer registers this with the
    // identical (mapSeed, isWalkable, findTarget, tryResolveTarget, attack) wiring it builds the basicRoamer entry with).
    // MONSTER-BEHAVIOR P5: the charge dep is threaded straight through to the base (GameServer registers the skirmisher
    // with the SAME BeginMonsterCharge wiring as the basicRoamer). A wounded skirmisher still FLEES, never charges — the
    // base StepChase runs the flee hook BEFORE the charge trigger, so flee precedence holds with no extra code here.
    public SkirmisherBehavior(
        int seed,
        Func<TileCoord, bool> isWalkable,
        FindTargetDelegate findTarget,
        TryResolveTargetDelegate tryResolveTarget,
        AttackDelegate attack,
        TryChargeDelegate? tryCharge = null)
        : base(seed, isWalkable, findTarget, tryResolveTarget, attack, tryCharge)
    {
    }

    // Flee iff the type authored a flee threshold (FleeHealthPct > 0) AND the monster is at/below it. The flee target is
    // recomputed every tick: directly AWAY from the target, a fixed flee distance out, so the gnoll keeps running while
    // the player chases. The distance only sets a clear away-HEADING for the locomotion — the resolver slides/stops it
    // at walls, so an unreachable far point is harmless (the gnoll glides as far that way as it can each tick).
    protected override bool TryChooseFleeTarget(WorldEntity monster, in MonsterAiTunables t, WorldVector targetPos, out WorldVector fleeTarget)
    {
        fleeTarget = default;
        if (t.FleeHealthPct <= 0d)
        {
            // The type doesn't flee (e.g. a skirmisher mis-authored with no threshold). Behaves as a basic roamer.
            return false;
        }

        if (monster.Stats.Health > t.FleeHealthPct * monster.Stats.MaxHealth)
        {
            // Above the wound threshold — press the attack like a basic roamer (the override is inert).
            return false;
        }

        // Wounded: run away. Heading = away from the target (recomputed each tick so it keeps fleeing as the target
        // chases). Zero-vector guard: if the target is exactly on the monster (degenerate), fall back to the monster's
        // current facing so we still have a valid away-heading (Facing is always a real Direction8, never zero).
        var dir = (monster.Position - targetPos).Normalized();
        if (dir == WorldVector.Zero)
        {
            dir = monster.Facing.ToUnitVector();
            if (dir == WorldVector.Zero)
            {
                dir = new WorldVector(1d, 0d); // ultimate fallback (unreachable in practice — Facing is never zero).
            }
        }

        // Flee distance: the chase leash (a few world units beyond aggro), so the away-target is well clear and the
        // glide has a definite heading. Falls back to the aggro radius if no leash is configured. The resolver handles
        // walls, so this is only a heading + an upper bound on how far one tick's move aims, never a teleport.
        var fleeDistance = t.ChaseLeash > 0d ? t.ChaseLeash : t.AggroRadius;
        fleeTarget = monster.Position + (dir * fleeDistance);
        return true;
    }
}
