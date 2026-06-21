# HUD & UI Design — client presentation layer

Status: design note / plan. Owner: Orchestrator. Branch: `ui/hud` (base `ef4f796`).

Goal: build the in-game HUD from the approved mockup as a **pure client-side presentation layer** in
the Godot client. This branch makes **zero server changes and zero protocol changes**. See the
**Guardrail** section — it is the binding constraint for every task here.

## Source of truth: the mockup

The approved mockup (provided by the human) defines these regions:

- **Bottom-center action bar**, left→right:
  - **Character portrait** (circular) with a small mount/cursor sub-badge. Three states:
    `character_only`, `character_and_mount`, `character_low_health` (red ring + worried face).
  - **2 consumable slots** — keys `1`, `2` — health potion (count badge, e.g. `12`) and resource
    potion (`4`). Bottom-right stack count.
  - **2 autoattack slots** — bound to **left / right mouse click** — `Auto_attack`, `Heavy_auto_attack`.
  - **4 spell slots** — keys `Q`, `E`, `F`, `R` — `Simple_spell`, `Advanced_spell`, `Defensive_spell`,
    `Ultimate_spell` (Ultimate shown selected/active with an amber frame).
  - **Vitals**: two horizontal bars above the spell row — **green = health**, **blue = resource**.
- **Top-right minimap**: square framed panel, player **arrow** (facing) + **dot** (position).
- **Center inventory window** (toggleable): title bar "Inventory" + close `X`; left sidebar tabs
  **Gathering / Building / Gear / Consumables**; a **6×3 slot grid**; item icons with stack counts.

### Slot states (from the asset sheet)

Each ability/consumable slot is a small state machine driven by **client-side cooldown timers**:

- **Empty slot frame** (`Spell Active Slot`, `Spell Cooldown Slot`, autoattack frames, consumable slot).
- **Ready/active**: bright icon, optional selected frame (amber, as on the Ultimate).
- **On cooldown**: darkened icon + a **radial/diagonal sweep** wiping bottom→top, with a **countdown
  number** centered (e.g. `15`, `9`). The sheet provides the sweep "parts from bottom to top".

## Architecture (Orchestrator decision)

**Build the HUD as Godot `.tscn` scenes with thin C# controllers in a new `UI/` folder**, decoupled
from `MmoClientRoot.cs`. Rationale: `MmoClientRoot.cs` is already ~2,500 lines and owns the
networking/movement/visuals; the HUD must not grow inside it. Scenes are also designable in-editor and
let us drop the provided art directly.

```
src/Mmo.Client.Godot/
  UI/
    Hud.tscn / Hud.cs            root CanvasLayer; owns child panels; reads HudState
    ActionBar.tscn / .cs         portrait + consumables + autoattacks + spells + vitals
    SlotButton.tscn / .cs        reusable slot: icon, keybind label, count, cooldown sweep+number
    VitalsBar.tscn / .cs         health + resource bars
    Portrait.tscn / .cs          3-state portrait
    InventoryWindow.tscn / .cs   tabbed window + 6×3 grid (replaces the S39 programmatic panel)
    Minimap.tscn / .cs           framed minimap, local-player arrow + dot
    HudState.cs                  client-side view-model (the single seam, see below)
  content/ui/                    art drop zone (see Asset Manifest)
```

### The `HudState` seam — how the HUD gets data without server work

The HUD renders **only** from a client-side `HudState` view-model. `MmoClientRoot` (or a small adapter)
populates it from data **already available on the client**; everything not yet replicated is **stubbed
with local/placeholder values** and a clear `TODO(server)` marker. This is what keeps the branch off the
server entirely.

| HUD element | Data source on this branch | Real wiring (future, NOT this branch) |
|---|---|---|
| Inventory grid + counts | **Real** — existing `InventoryUpdate` + client item registry (S37–S39) | already real |
| Consumable counts (1,2) | **Real** if those items exist in inventory; else stub | — |
| Health / resource bars | **Stub** — local fields + a debug control to vary them | server vitals/stat replication |
| Spell/autoattack cooldowns | **Local timers** — start a timer on keypress/click | server-authoritative ability results |
| Portrait state | **Derived** from stubbed health (low-health <25%); mount stub | mount/health from server |
| Minimap arrow + dot | **Real** — local player position/facing already client-side | other-entity blips via AOI |

No `HudState` field reads or writes movement-replication state. The minimap uses the **local** player's
already-known position/facing (read-only), not the snapshot/AOI pipeline.

## Guardrail — binding constraint (the human's explicit requirement)

> "Avoid touching anything server side about movement replication."

Concretely, on the `ui/hud` branch:

- **OFF-LIMITS — do not modify:**
  - `src/Mmo.Server/**` (the entire server — especially movement, snapshots, AOI, tick loop).
  - `docs/protocol.md` and any wire/message types in `src/Mmo.Shared/**`. **No protocol bump.**
  - Movement / snapshot / prediction / reconciliation / interpolation code paths inside
    `MmoClientRoot.cs` and any `Movement*` / snapshot handlers. The HUD reads client state; it must not
    alter how movement is received, predicted, reconciled, or rendered.
- **ALLOWED:**
  - New files under `src/Mmo.Client.Godot/UI/**` and assets under `content/ui/**`.
  - A **minimal, additive** hook in `MmoClientRoot.cs` to instantiate `Hud.tscn` and feed `HudState`
    from already-exposed, **read-only** client state. No refactors of movement code to do it.
  - Client-only tests (`Mmo.Client.Core.Tests`) for HUD logic (cooldown math, state derivation).
- **Rule of thumb:** if a task seems to need server data (real health, real abilities), **stub it in
  `HudState`** and leave a `TODO(server)` — do not reach into the server or protocol. If a slice truly
  cannot be done client-only, raise it as a new `todo/` item rather than crossing the line.

## Asset Manifest — where the human drops the provided art

Create these folders and drop the exported PNGs (the human will provide icon/asset files):

```
src/Mmo.Client.Godot/content/ui/
  icons/abilities/   auto_attack, heavy_auto_attack, simple_spell, advanced_spell,
                     defensive_spell, ultimate_spell
  icons/consumables/ health_potion, resource_potion
  frames/            spell_active_slot, spell_cooldown_slot, autoattack_active,
                     autoattack_cooldown, consumable_slot, selected_frame (amber),
                     cooldown_sweep_*  (the bottom→top parts)
  portraits/         character_only, character_and_mount, character_low_health
  minimap/           minimap_frame, player_arrow
```

Note: assets added outside the editor need a headless import pass — the visual-check script already runs
`--headless --import`. Prefer running through `start-godot-visual-check.cmd` after dropping new art so
`.import` sidecars are generated (otherwise `GD.Load` returns null at runtime).

## Sequencing (promote to `todo/` one at a time as each lands)

Each slice is one commit-sized task. **S-HUD-1 lands first — everything else depends on its
architecture.** Only S-HUD-1 is queued now (`todo/S107-hud-foundation.md`); the rest are promoted as
the foundation settles, so we don't spec downstream tasks against an architecture that's still moving.

1. **S-HUD-1 — Foundation / scaffold** *(queued)*: `UI/` folder, `Hud.tscn` root `CanvasLayer` mounted
   by `MmoClientRoot` (additive hook only), `HudState` view-model with stubbed fields + a debug way to
   vary them, `content/ui/**` folders, asset-import notes. Empty/placeholder HUD renders; movement code
   untouched; `run-checks` green.
2. **S-HUD-2 — Vitals + portrait**: health/resource bars + 3-state portrait, driven by `HudState`.
3. **S-HUD-3 — Action bar + slot state machine**: `SlotButton` (icon, keybind label, count, cooldown
   sweep + countdown number), wired for 2 consumables (1,2), 2 autoattacks (LMB/RMB), 4 spells
   (Q/E/F/R). Cooldowns driven by local timers on input. Unit-test the cooldown/number math.
4. **S-HUD-4 — Inventory window rework**: tabbed window (Gathering/Building/Gear/Consumables), 6×3
   grid, icons from the registry, counts, close `X`, toggle hotkey. Replaces the S39 programmatic panel,
   reading the same `InventoryUpdate` data.
5. **S-HUD-5 — Minimap**: top-right framed minimap, local-player arrow (facing) + dot (position),
   read-only from local client state.

## Deferred (explicit non-goals for this branch)
Real combat/health/mana replication; real ability system + server-authoritative cooldowns; mount
system; other-entity minimap blips; item drag-and-drop / equip; controller/rebindable keys. These all
imply server or protocol work and are out of scope behind the guardrail.
