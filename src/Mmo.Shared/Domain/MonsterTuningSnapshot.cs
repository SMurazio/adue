using System.Collections.Generic;

namespace Mmo.Shared.Domain;

// LIVING-ENEMIES P2-POLISH (protocol v33): server->client replication of the per-monster-TYPE tuning. Monster AI
// tuning is server-authoritative (read each AI pass by MonsterRoamAi) and live-tunable via AdminSetTuning on the
// per-type "<typeId>.<field>" keys; this snapshot ships the CURRENT per-type values so the client's F1 "Monster" tab
// can list the types (dropdown) and show + edit the authoritative numbers — exactly as CombatTuningSnapshot does for
// the combat.* knobs. Sent on login (initial truth) and broadcast to all clients whenever a per-type key changes.
//
// Unlike the combat snapshot, the client derives NO simulation from these values (the monster's movement/attacks are
// pure server-side AI the client just interpolates) — the snapshot exists ONLY so the admin tuning panel can show +
// edit the live per-type values. A non-admin client receives it harmlessly and simply never opens the panel.
public readonly record struct MonsterTuningSnapshot(IReadOnlyList<MonsterTypeSnapshot> Types);

// One monster type's replicated tuning. Values are in the SAME ms/tile units as the per-type registry keys
// (<typeId>.roamRadius, .moveSpeed, .maxHealth, …) so the F1 fields round-trip 1:1 through AdminSetTuning. Id is the
// stable wire/registry id; DisplayName is the human label shown in the dropdown.
public readonly record struct MonsterTypeSnapshot(
    string Id,
    string DisplayName,
    int MaxHealth,
    double MoveSpeedMultiplier,
    int RoamRadius,
    int PauseMinMs,
    int PauseMaxMs,
    int AggroRadius,
    int ChaseLeash,
    int AttackRange,
    int AttackDamage,
    int AttackCooldownMs);
