using System.Collections.Generic;

namespace Mmo.Shared.Domain;

// LIVING-ENEMIES P2-POLISH (protocol v33; reshaped DATA-DRIVEN at v40): server->client replication of the per-monster-
// TYPE tuning. Monster AI tuning is server-authoritative (read each AI pass by the monster behavior) and live-tunable via
// AdminSetTuning on the per-type "<typeId>.<field>" keys; this snapshot ships the CURRENT per-type values so the
// client's F1 "Monster" tab can list the types (dropdown) and show + edit the authoritative numbers — exactly as
// CombatTuningSnapshot does for the combat.* knobs. Sent on login (initial truth) and broadcast to all clients
// whenever a per-type key changes.
//
// Unlike the combat snapshot, the client derives NO simulation from these values (the monster's movement/attacks are
// pure server-side AI the client just interpolates) — the snapshot exists ONLY so the admin tuning panel can show +
// edit the live per-type values. A non-admin client receives it harmlessly and simply never opens the panel.
//
// DATA-DRIVEN (v40): a type no longer carries a fixed struct of named fields; it carries a generic LIST of
// MonsterTuningField descriptors (Key/Label/Value/Min/Max/IsInteger). The server builds this list ONCE from the
// MonsterTypeRegistry's tunable descriptors, and the F1 tab renders one labelled row per field — so exposing a NEW
// knob is a single server-side registration, with NO protocol bump, NO UI edit, NO new record field.
public readonly record struct MonsterTuningSnapshot(IReadOnlyList<MonsterTypeSnapshot> Types);

// One monster type's replicated tuning. Id is the stable wire/registry id (the "<typeId>." prefix the AdminSetTuning
// per-type keys are built from); DisplayName is the human label shown in the F1 dropdown; Fields is the generic list
// of tunable knobs (in the registry's authored display order).
public readonly record struct MonsterTypeSnapshot(
    string Id,
    string DisplayName,
    IReadOnlyList<MonsterTuningField> Fields);

// One tunable knob of a monster type, replicated generically so the F1 tab renders + edits it without per-field code.
//   Key       — the registry suffix after "<typeId>." ("moveSpeed", "roamRadius", "hopDistance", …); the tab sends
//               AdminSetTuning("<typeId>.<Key>", parsed) on Apply, so it round-trips 1:1 through the registry.
//   Label     — the human caption shown next to the row ("hop distance (tiles)").
//   Value     — the CURRENT post-clamp authoritative value (the field is seeded from this).
//   Min, Max  — the registry clamp bounds (shown as a hint; the server clamps authoritatively regardless).
//   IsInteger — true ⇒ the value is a whole number (the tab displays/parses it without a fractional part).
public readonly record struct MonsterTuningField(
    string Key,
    string Label,
    double Value,
    double Min,
    double Max,
    bool IsInteger);
