namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P2-POLISH: a named monster TEMPLATE — its own stats + AI tuning, owned server-side. A spawned
// monster (EntityKind.Monster) remembers its type id; the AI reads its Tunables and its SpeedMultiplier FROM the
// type each tick, instead of a single global monster.* block. This is the seam that lets the world hold more than
// one kind of monster (slime now; more later) each with its own feel, all live-tunable.
//
// LIVE-TUNABLE: the per-type values are MUTABLE so an admin can retune one type at runtime via the AdminSetTuning
// path on PER-TYPE keys (e.g. "slime.roamRadius", "slime.aggroRadius"). The MonsterTypeRegistry owns the table of
// types + the apply/clamp logic (mirroring ServerTuningRegistry), and the per-type values are REPLICATED to clients
// (MonsterTuningSnapshot) so the F1 Monster tab can show + edit the authoritative numbers. They are read fresh each
// AI pass, so a live change takes effect on the next tick with no torn state (single-threaded tick loop).
//
// Defaults migrate the former global monster.* block (roam 4 / pause 2000-5000 / aggro 6 / leash 12 /
// attackRangeUnits 1.5 / attackDamage 10 / attackCooldown 1000), EXCEPT MoveSpeedMultiplier, which is the slower-than-
// player default (0.6 → the slime steps ~417 ms vs the player's 250 ms base, so the dumb ones are clearly
// outrunnable), and MaxHealth (100, the CharacterStats default, now an explicit per-type knob so a tankier type
// is one number away).
public sealed class MonsterType
{
    public MonsterType(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    // The wire/registry id (lowercase, stable) the AdminSetTuning per-type keys are built from ("slime" → "slime.*").
    public string Id { get; }

    // The human-facing name shown in the F1 Monster-tab dropdown ("Slime").
    public string DisplayName { get; }

    // MONSTER-BEHAVIOR P1 (docs/monster-behavior-design.md): the COMPOSITION selector for this type's LOCOMOTION
    // ("body") — the id of the IMonsterLocomotion the AI drives this type's movement through. GameServer owns a
    // registry of locomotions keyed by this id (only "hop" exists today) and resolves a type to its locomotion each
    // tick; an unknown id falls back loud-but-safe to "hop" (resolution + fallback are GameServer's job, NOT this
    // loader's — the registry just STORES the string). Server-side type DATA only: it is NOT a numeric tunable (not in
    // the F1 descriptor list / not replicated) and NOT on the wire — it is set only via the manifest. Default "hop"
    // so every existing/omitted type keeps hopping. This is the seam P2 (GlideLocomotion + a walker type) builds on.
    public string LocomotionId { get; set; } = "hop";

    // MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): the COMPOSITION selector for this type's BEHAVIOR
    // ("brain") — the id of the IMonsterBehavior that runs this type's roam/chase/attack decisions. GameServer owns a
    // registry of behaviors keyed by this id (only "basicRoamer" exists today) and resolves a type to its behavior each
    // tick; an unknown id falls back loud-but-safe to "basicRoamer" (resolution + fallback are GameServer's job, NOT
    // this loader's — the registry just STORES the string). Server-side type DATA only: it is NOT a numeric tunable (not
    // in the F1 descriptor list / not replicated) and NOT on the wire — set only via the manifest. Default "basicRoamer"
    // so every existing/omitted type keeps the current brain. This is the seam P4 (a second behavior) builds on.
    public string BehaviorId { get; set; } = "basicRoamer";

    // Per-type max HP — the monster spawns at full of this. Default 100 (the CharacterStats default).
    public int MaxHealth { get; set; } = 100;

    // MONSTER-BEHAVIOR P4 (docs/monster-behavior-design.md): the wounded-flee threshold as a FRACTION of MaxHealth in
    // [0,1], consumed by a SkirmisherBehavior brain. Default 0 = NEVER flee (every BasicRoamer type ignores it). A
    // skirmisher flees when Health <= FleeHealthPct*MaxHealth (e.g. 0.3 = flee below 30% HP). Behavior-specific DATA
    // only: it is NOT a live F1 tunable / NOT in the descriptor table / NOT replicated (the global F1 Monster tab would
    // otherwise show a flee knob on non-fleers) and NOT on the wire — set only via the manifest (clamped to [0,1]). A
    // later tiny follow-up can promote it to a live knob if the feel-test wants it.
    public double FleeHealthPct { get; set; }

    // MONSTER-BEHAVIOR P5 (docs/monster-behavior-design.md): the per-type ABILITY composition selector — the ids of the
    // shared action-executor abilities this type can use, the abilities dimension of the §2 composition model. Default
    // EMPTY (no abilities; every BasicRoamer/slime type). The gnoll carries ["charge"]. Server-side type DATA only: NOT
    // a numeric F1 tunable / NOT replicated / NOT on the wire — set only via the manifest. The BEHAVIOR reads it (a
    // skirmisher charges only if this contains "charge" AND ChargeCooldownMs > 0), so an ability not in the set is inert
    // even if its tuning is authored. A genuinely-new ability is a new code primitive + an id here, per the design.
    public List<string> AbilityIds { get; set; } = [];

    // MONSTER-BEHAVIOR P5: the CHARGE ability tuning (a fast forward dash through the shared executor to close the gap).
    // ChargeCooldownMs 0 = NO charge (every non-gnoll type; combined with AbilityIds not containing "charge" the brain's
    // ChargeEnabled is false → the charge trigger is inert). ChargeDistanceUnits = how far the dash travels (world units,
    // grounded — jumpHeight 0). ChargeTriggerRangeUnits = the MAX target distance at which the brain fires a charge (it
    // charges only when the target is OUT of attack range but within this — i.e. the gap is worth closing). Behavior-
    // specific DATA only (like FleeHealthPct): NOT a live F1 tunable / NOT replicated / NOT on the wire — set only via
    // the manifest (clamped to sane bounds by FromManifestJson). Defaults 0 so an omitted/non-charger type never charges.
    public int ChargeCooldownMs { get; set; }
    public double ChargeDistanceUnits { get; set; }
    public double ChargeTriggerRangeUnits { get; set; }

    // INTERNAL-ONLY (no longer user-tunable / shown on the F1 Monster tab — the confusing "move speed (x)" knob was
    // retired in favour of the intuitive RANGE / HEIGHT / AIRBORNE / DELAY hop knobs). This is still the multiplier of
    // the player's base cadence (< 1 = slower) that the entity's replicated SpeedMultiplier is seeded from AT SPAWN —
    // it sets the client-side interpolation cadence (EntitySpawn / MovementSpeedChanged) only. It NO LONGER drives the
    // monster's HOP cadence: StepMonsterAi now paces hops off HopAirborneMs + HopDelayMs (see GameServer.StepMonsterAi).
    // Default 0.6 — kept so the replicated interp cadence is unchanged. Not replicated as a tunable, not clamp-edited.
    public double MoveSpeedMultiplier { get; set; } = 0.6;

    // P1 roam knob. CONTINUOUS: a world-unit RANGE (Euclidean, fractional) — the AI samples a continuous disc of this
    // radius; no tile quantization. Authored 4 == the old 4-tile leash (1 unit == 1 old tile). PauseMs stay integer ms.
    public double RoamRadius { get; set; } = 4d;
    public int PauseMinMs { get; set; } = 2000;
    public int PauseMaxMs { get; set; } = 5000;

    // P2 aggro/chase knobs — CONTINUOUS world-unit RANGES (Euclidean, fractional), used DIRECTLY by the AI's distance
    // tests. Authored 6 / 12 == the old tile values (1 unit == 1 old tile). AttackDamage/CooldownMs stay integer.
    public double AggroRadius { get; set; } = 6d;
    public double ChaseLeash { get; set; } = 12d;
    public int AttackDamage { get; set; } = 10;
    public int AttackCooldownMs { get; set; } = 1000;

    // CONTINUOUS MIGRATION (Phase 8): the discrete LEAP distance of one hop, in world units (range). The AI hops this far toward
    // its continuous nav target each move-cadence window; the resolver slides/stops it at walls. DATA-DRIVEN tuning:
    // default bumped 1.0 → 1.5 (the user's "range too low" feel-test complaint) — modest, and now live-tunable per type
    // on the F1 Monster tab ("slime.hopDistance"), so it can be dialed further without a code change.
    public double HopDistanceUnits { get; set; } = 1.5;

    // MOVEMENT-ACTIONS (Phase C): the apex height (world units) of the slime's REAL ballistic hop. The hop is now a
    // genuine Jump driven by the shared ServerActionExecutor — a real, replicated VerticalOffset arc — so this height
    // is server-authoritative and rides the wire (remote clients render the arc from VerticalOffset), REPLACING the
    // retired client-only cosmetic HopHeight. Default 0.5 == the old cosmetic MonsterHopPeakHeight, so the visible
    // bounce height is unchanged; live-tunable per type ("slime.hopHeight") alongside the other knobs.
    public double HopHeightUnits { get; set; } = 0.5;

    // DATA-DRIVEN tuning (the "hops too often" fix): how long ONE hop is in the air, in ms. BeginMonsterHop builds the
    // ballistic Jump's DurationTicks from this (not from the move cadence), so the hop is a SHORT airborne span and the
    // slime RESTS on the ground for (cadence − airborne) ticks before the next hop starts. The move cadence (moveSpeed)
    // still controls how OFTEN hops start; this controls how long each one lasts. Default 300 ms; live-tunable per type
    // ("slime.hopAirborneMs"). Keep airborne < cadence for real rest (the IsActive gate keeps it safe either way).
    public int HopAirborneMs { get; set; } = 300;

    // SLIME-FEEL-POLISH: the grounded REST between hops, in ms — the real, intuitive "delay between each jump" the user
    // asked for (the opaque "move speed (x)" knob is retired). The monster's hop CADENCE (time between hop starts) is
    // now HopAirborneMs + HopDelayMs: it hops (airborne for HopAirborneTicks), lands, then sits IDLE on the ground for
    // HopDelayTicks before the next hop. Default 400 ms — so the default cycle is 300 airborne + 400 rest = 700 ms, a
    // VISIBLE pause between hops. Live-tunable per type ("slime.hopDelayMs"); 0 = hop again the instant it lands.
    public int HopDelayMs { get; set; } = 400;

    // CONTINUOUS: the world-unit RANGE (Euclidean, fractional) at which the monster ATTACKS instead of hopping. The AI
    // reads THIS and the F1 "attack range" knob edits it (the former integer-tile AttackRange knob — which the AI never
    // read — is retired). Default 1.5 — the √2-covering of the old 1-tile (3×3 Chebyshev) adjacency, so a diagonal
    // player at Euclidean √2 ≈ 1.41 still counts as adjacent.
    public double AttackRangeUnits { get; set; } = 1.5;

    // LIVING-ENEMIES P3: how long after a monster of this type DIES its spawner waits before spawning a fresh
    // full-HP one at the spawner tile. Default 5000 ms (~5 s). Live-tunable via the "<typeId>.respawnMs" key on the
    // F1 Monster tab; read live by the spawner at death time.
    public int RespawnMs { get; set; } = 5000;

    // LOOT P4a: the LootTableRegistry id this type rolls on death. Empty string = no loot (the explicit
    // "this type drops nothing" sentinel — distinct from an unknown id). STATIC seed data for now: it is
    // NOT live-tunable or replicated, unlike the AI knobs above, because it is content authored once, not a
    // feel dial an admin retunes mid-session. (If later loot wants live retuning — e.g. an event drop boost
    // — promote it to a per-type key like the others; flagged in the P4a review-request.)
    public string LootTableId { get; set; } = string.Empty;
}
