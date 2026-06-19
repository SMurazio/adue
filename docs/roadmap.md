# Roadmap / Prioritized Backlog

The single prioritized map of outstanding work, drawn from the UO-inspired study
(`movement-and-architecture-notes.md`), the client-architecture plan (`client-architecture-design.md`), and
the standing `todo/` queue. **Active work lives as `todo/S<n>` files; future/optional items are mapped here
until promoted.** Promoting an item = create its `todo/` file; each shipped change is its own commit.

Tiers: **Now** (in flight) · **Next** (clear value, ready to queue) · **Later** (valuable, gated on a
trigger) · **Options** (opportunistic / only if a trigger appears).

---

## Now — in flight
- _(nothing in flight — S63 free-turns shipped; next pickup is below)_

## Next — clear value, queue when ready
1. **Walk / Run movement** — two cadences (walk + run) with run-by-cursor-distance; the second movement
   feel win. Needs a small protocol bump (a `Running` flag on `MoveIntent` + how remote viewers learn an
   entity's walk/run cadence). **Hold until free-turns is felt**, then spec + queue. — notes §1.2.
2. **Decorative placement of Portal + House.** The assets are imported + wired (`ModelVisual`/`SpriteVisual`)
   but invisible — they need a server-side content/decoration entry to appear. Small; closes the S61 loop.
3. **Client refactor Stage 2 — `VisualArchetype` on the wire.** Replace the fragile `DisplayName=="Rock"`
   dispatch with a stable server-sent rendering id. Unblocks clean content growth (reskins/renames stop
   breaking rendering). — `client-architecture-design.md` Stage 2.

## Later — valuable, gated on a trigger
- **Client refactor Stage 3 (`VisualCatalog`, data-driven assets)** then **Stage 4 (decompose
  camera/input/HUD + Godot InputMap).** Do when `MmoClientRoot` friction or content volume justifies it.
  — client-architecture-design.md Stages 3–4.
- **AOI spatial index** — coarse fixed cells + intrusive per-cell lists + ring-expanding, nearest-first,
  early-outable, zero-alloc range query. **Trigger:** when the AOI scan is the measured bottleneck (capacity
  work). Targets a limiter our capacity study already named. — notes §2.
- **Snapshot bandwidth** — delta-coded snapshot encoding (`todo/S47`) + outgoing-stream compression
  (LZ4/Huffman). **Trigger:** when per-client bandwidth is the measured limit at high visible density.
  Group these two. — notes §5, `todo/S47`.
- **Client perf polish** — finalize vsync / residual frame spikes (`todo/S28`); per-chunk render cull
  (`todo/S36b`). Do when frame pacing or draw load needs it.

## Options — opportunistic / future
- **Client refactor Stage 5** — `.tscn` scenes + an art workflow. When editor-authored art arrives.
- **EffectsLayer** — transient, non-entity visuals (harvest/hit/telegraph/floating-number). When combat or
  richer feedback lands. — client-architecture-design.md cross-cutting.
- **`WorldSpace` coordinate helper** — centralize tile↔world / iso projection. Opportunistic cleanup.
- **Movement: explicit per-step correction** (vs our position-inference reconcile) — only if prediction
  needs hardening. — notes §1 takeaway 4.
- **Movement: RTT-scaled reconcile tolerance** — minor refinement over the fixed 3-tile tolerance.
- **Persistence: compact-binary + background-thread saves** — only if SQLite write-behind ever stalls the
  tick at scale. — notes §4.
- **Strongly-typed entity id** (`readonly struct` over raw `uint`) — trivial; only while touching that code.

---

## Already done (context)
- **S63 — Free turns** (turn costs a tunable turn-delay, not a full step cooldown; protocol v18). Committed.
- **S61 — entity-visual hierarchy + `EntityRenderer`** (client refactor Stage 1). Committed.
- **S62 — UO-inspired study.** Closed → `movement-and-architecture-notes.md`.
- **S60 tuning panel · S59 turn-then-move · S58 rock models · S56/S57 mouse + labels · S51 per-entity
  speed · S53 prediction.** Committed (see git history).
