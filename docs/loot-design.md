# Loot — Design

Loot turns a kill into a reward. Drops are **resources** (crafting materials), common→rare, delivered via an
**openable corpse**, with **eligibility** tracked from damage contribution so fair group loot is a later
*config* change, not a rewrite. No gear/equipment drops yet (item system undefined); no gold/vendor sink — the
sink is the **future crafting system** (resources are its materials), so loot has a point the day it exists.

## Principles — what makes loot feel meaningful
- **Predictable floor + a rare tail.** Every kill gives *something* (a common mat); rarely a kill gives a rare
  mat. Variance is the dopamine.
- **Legible rarity.** A drop's worth reads at a glance (common→legendary, colour-coded). The rarity readout is
  the feeling.
- **Identity.** Drops say what you killed; a few monsters have a *signature* rare (the reason to farm them).
- **A real use.** Resources are crafting materials — the meaning is built in, even before crafting ships.
- **A juicy moment.** Rare drops announce themselves (rarity-coloured in the loot window; later a sound/beam).

## Loot tables — composable, reusable, referenced by id
- `LootTableRegistry`: `lootTableId → LootTable`. Defined ONCE, shared across monsters (the user's call: tables
  are first-class, a monster *references* one — no per-monster duplication).
- A `LootTable` is an ordered list of **drops**, each resolved **independently**. A drop is one of:
  - **fixed** — `{ resourceId, chance, minQty, maxQty }` (the guaranteed/chance floor; guaranteed = `chance 1.0`),
  - **weightedPick** — roll once, pick one of N options by weight (+ an empty weight = "no drop"); an option may
    be a resource OR a nested **tableRef**,
  - **tableRef** — roll another table (nesting → shared rarity pools; add a rare to one pool, it drops from every
    table that nests it).
- `MonsterType.lootTableId` references a table. Many types → one table. Empty/none → no loot.
- Rolled on `KillMonster`, **server-side + seeded** (deterministic → testable; later a magic-find/luck modifier
  rides the same roll context).

Example (resources only; mapped to real resource ids at build time):
```
rare_material_pool  (weightedPick, one of):
    <rare resource A>   w 60
    <rare resource B>   w 40
slime_loot:
    <common resource>   chance 1.00   qty 1–3      # floor — always a mat
    →rare_material_pool  chance 0.004               # ~0.4% → the shared rare tail
    <slime signature>    chance 0.0008  qty 1       # this monster's chase mat (optional)
```

## Rarity
Items/resources carry a `rarity` tier (common / uncommon / rare / epic / legendary). Drives the loot-window
colour + the drop-moment emphasis. Rare resources are the chase. (Reuse an existing quality concept if one
exists, else add a `rarity` field to the item/resource definition.)

## Eligibility + the fair-group-loot groundwork (lay it now)
Fair loot needs to know **who earned the kill**. Cheap to lay now, expensive to retrofit.
- **Contribution ledger** per monster: record damage dealers (who, and how much) as they hit it. On death → the
  **eligible-looter set** (solo = the killer).
- The corpse is tagged with `eligibleLooters` + a `lootMode` enum — **FFA-among-eligible** now; round-robin /
  **personal** / need-greed / master-looter later: a new mode over the *same* eligibility data.
- **Personal-loot-ready.** Contributors known + a single roll-site → later swap *roll-once* for
  *roll-per-eligible-player* (everyone opens the corpse, sees their OWN instanced loot, zero contest — the
  modern fair default). A config flip, not surgery.

## Corpse — delivery (UO-style, openable)
- On death, spawn a **Corpse** entity at the death tile holding the rolled resources + `eligibleLooters` +
  `lootMode` + a decay timer. Replicated (AOI), rendered as a corpse/bag on the ground.
- Walk up → interact → **loot window** showing the corpse inventory (rarity-coloured). Take item / loot-all →
  transfer to the player inventory, **server-validated against eligibility**. Empty or decay-expired → despawn.
- Decay ~minutes (UO-like), tunable.

## Staging
- **P4a — loot engine (this branch).** `LootTable`/`LootEntry`/`LootTableRegistry` (fixed / weightedPick /
  nested tableRef), `rarity` on items/resources, `MonsterType.lootTableId`, the seeded roll wired into
  `KillMonster` (produces the rolled resource stacks; **held/logged, not delivered live** — P4b consumes it).
  Fully headless-tested. Foundation; nothing visible yet.
- **P4b — corpse + eligibility.** Corpse entity (consumes P4a's roll) + the contribution→eligibility ledger +
  decay + AOI replication/render + interact → loot-all (eligibility-gated). First playable loot.
- **P4c — loot window.** Open the corpse → see its inventory → take individual / loot-all; rarity-coloured.

## Open questions
- Resource rarity: reuse an existing quality/tier or add a `rarity` field (P4a decides + flags).
- Corpse decay duration (default ~2–5 min, tunable) — P4b.
- `lootMode` default = FFA-among-eligible; **personal loot** the eventual fair default (groundwork only now).
