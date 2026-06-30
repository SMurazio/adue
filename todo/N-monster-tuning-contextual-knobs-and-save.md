# N — F1 Monster tab: contextual per-composition knobs + a Save button

User (looking at the F1 Monster tab on the gnoll): the gnoll shows HOP knobs it doesn't use (it GLIDES) — the knobs
should be CONTEXTUAL to the monster's locomotion/behavior/abilities. Also wants a SAVE button to persist live-tuned
values. Currently the descriptor table (`MonsterTypeRegistry.Descriptors[]`) is GLOBAL (same knobs for every type), so a
glider shows irrelevant hop knobs AND its real knobs (walk speed, flee %, charge params) are hidden.

## Part A — contextual knobs (no protocol change; the snapshot is already a per-type variable field list)
Tag each descriptor with a CATEGORY and include it in `BuildSnapshot(type)` only when it applies to that type:
- **common** (always): hp, roam range, aggro range, chase leash, attack range/damage/cooldown, pause min/max, respawn.
- **locomotion=hop** (slime): hop distance / height / airborne / delay.
- **locomotion=glide** (gnoll): WALK SPEED (expose `MoveSpeedMultiplier` — currently internal; for a glider it IS the
  walk speed; hidden for hoppers where it only affects the dormant interp cadence).
- **behavior=skirmisher**: flee health % (`fleeHealthPct`, P4 — currently manifest-only).
- **ability=charge**: charge cooldown / distance / trigger range (P5 — currently manifest-only).
Needs: a category tag on `TunableDescriptor`; the per-type filter in `BuildSnapshot` (match the type's LocomotionId /
BehaviorId / AbilityIds); new descriptors + `TryApply` cases + clamps for walk-speed / fleeHealthPct / charge params.
`IsMonsterTypeKey` must still recognise the new keys. The gnoll then shows ONLY its relevant knobs; the slime keeps hop.

## Part B — Save button (persist the live values to the manifest)
F1 Monster tab gets a **Save** button (next to Apply). It sends a NEW admin/dev message → the server SERIALIZES the
current `MonsterType` values back to JSON (the inverse of `FromManifestJson`) and WRITES `monsters.json`, so a restart
picks up the tuned values (completing the P0 data loop — today Apply is in-memory only, lost on restart).
- New `MessageType` (additive protocol bump) + a handler, gated to admin/dev like the other tuning commands.
- WHICH file: write the file the server LOADS so a restart applies it. `AppContext.BaseDirectory/Content/monsters.json`
  is the loaded copy, but CopyToOutputDirectory may re-clobber it on rebuild — prefer writing the repo SOURCE
  (`src/Mmo.Server/Content/monsters.json`) if locatable, else the loaded copy; LOG the path written. Note: re-serialising
  via System.Text.Json DROPS the `//` comments — acceptable for a dev tool (or emit a header comment).

## Rigor
Server + Godot F1 UI change. Part A is mostly server (descriptor table) + the UI renders what it's sent (already
data-driven). Part B touches the protocol (new message) + writes a file (review the path + the serializer + the
admin-gating). Gate + independent review (the serializer round-trip: FromManifestJson(Serialize(types)) == types; the
contextual filter) + a human feel-test. SEQUENCED AFTER the monster-collision pass (same files). Builds on
[[monster-behavior-architecture]] (P0 manifest + the data-driven F1 tab).
