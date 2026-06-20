# Movement & Architecture Design Notes

Design notes for our Godot 3D client + held-intent C# server, **informed by studying mature tile-based MMO
clients and servers**. These are ideas adapted to our stack as *inspiration, not ports* — we keep our engine
and our model and borrow the good solutions. Each note that becomes a code change gets its own todo + commit.
(S62.)

---

## 1. Movement (the priority)

### Target feel (what long-lived tile MMOs converge on)
- **Two step cadences**: a deliberate **walk (~400 ms/tile)** and a snappy **run (~200 ms/tile)** (mounted
  faster). Player-chosen — that range is what makes movement feel both grounded and responsive.
- **A direction change steps immediately** (S98). There is no turn beat: pressing a new direction steps in
  that direction on the next eligible tick with facing set on the step. (Earlier S59/S63 turn-then-move +
  `move.turnDelayMs` were removed in S98 — they added a turn-beat of latency to every direction change and were
  the root of the spam-direction-change skew.)
- **Run vs walk by cursor distance** (mouse): hold-to-move toward the cursor, **run when the cursor is far**
  (~190 px from the player), walk when near. No dead-zone — movement starts immediately on hold.
- The client **predicts its own steps immediately**; the server is authoritative; on disagreement the client
  snaps to the server. A small bound on unconfirmed steps (~5) is plenty.

### Our takeaways (prioritized)
1. **[SUPERSEDED — S98 removed turn-then-move] Direction changes step immediately.** S59 turn-then-move
   charged a full step cooldown for a turn; S63 reduced that to a small tunable `move.turnDelayMs` (default
   80 ms). S98 removed the mechanic entirely: a direction change now steps immediately in the new direction
   with facing set on the step (no separate turn action, no `turnDelayMs` in server config / `ServerHello` /
   the predictor / the F4 panel). `WorldEntity.TryStep` and `LocalPlayerPredictor.Tick` set facing on the step
   and resolve move/blocked as before; a facing-only change (e.g. pressing into a wall) still bumps
   `StateRevision` so the new facing replicates. Protocol v20 dropped `ServerHello.turnDelayMs`.
2. **[HIGH — feel] Walk vs Run (two cadences), run-by-cursor-distance.** Our single ~150 ms speed lacks the
   walk/run range. Add a run cadence + the cursor-distance trigger (and/or a run key). → own todo.
3. **[VALIDATION — we're already ahead here] Our held-intent + server-paced model is anti-speedhack by
   design.** The common *client-driven* move model (client requests each step) forces the server into
   elaborate defenses — credit buffers, queue-depth analysis, RTT correlation, confidence-scored speed-hack
   detection. **We're server-driven**: the client sends a held *direction* and the **server** paces the
   steps, so a client physically cannot move itself faster. We get speed-hack immunity for free — don't
   build the throttle machinery.
4. **[CONSIDER — prediction hardening] Explicit per-step correction vs position inference.** The robust
   reconcile is an **explicit** server signal ("that step was rejected → snap"), rather than *inferring*
   divergence from snapshot position deltas (our current 3-tile-tolerance guess). Ours is fine for a
   held-intent model; if prediction ever needs hardening, explicit correction beats inference. Not now.
5. **[MINOR] RTT-scaled reconcile tolerance.** Sizing the jitter tolerance by measured round-trip time
   (more slack for laggy players) is a small, principled refinement over our fixed 3-tile tolerance.

**Net:** #1 (free turns) + #2 (walk/run) take movement from "close" to "right"; #3 is a design win we already
hold; #4/#5 are filed.

---

## 2. Server spatial index / AOI (high value — targets our known limiter)
Our own capacity notes already flag the **AOI scan at high visible density** as the real bottleneck. A
proven design for exactly this shape of problem:
- **Coarse fixed grid of cells** (e.g. 16×16 tiles), each cell holding an **intrusive linked list** of the
  entities currently in it. Add / remove / move-between-cells is **O(1) relink** — no per-entity scan, no
  allocation per move.
- **Range queries touch only the cells overlapping the query box**, visited in **rings expanding outward
  from the center cell**. That yields entities **roughly nearest-first**, so a visible-entity **cap keeps
  the closest** ones and the scan can **early-out** the moment the cap is hit — precisely what our AOI wants.
- **Zero-allocation enumeration** (a `ref struct` enumerator) plus a per-cell **version stamp** to detect
  mutation mid-iteration.

→ When we tackle AOI performance, move our spatial index to this shape (coarse cells + intrusive lists +
ring-expanding, nearest-first, early-outable, zero-alloc query). It attacks the limiter our capacity study
named. → own todo when AOI perf is on deck.

---

## 3. Client 2.5D rendering — mostly handled by being 3D
Pure 2D-isometric clients invest heavily in a painter's-algorithm depth sort (tile-depth + height + type
priority) and in culling roofs/foliage above the player. **We're 3D** — Godot's depth buffer orders opaque
geometry for free, so that whole layer is largely N/A. The carry-overs:
- **Deliberate transparency handling for 2.5D sprites** (the billboard house): alpha-scissor/cutout sidesteps
  transparency sort issues; a blended sprite needs an explicit render priority anchored to its ground tile.
  (Already captured in `client-architecture-design.md` → `SpriteVisual`.)
- **Optional later:** fade or cull objects between the camera and the player (the 3D analog of roof-hiding).

---

## 4. Server persistence — our write-behind is fine; one idea to bank
A proven high-throughput save path: serialize entities to a **compact binary format** and flush on a
**background worker thread** (off the tick loop). Our write-behind SQLite already keeps saves off the hot
path for now; the idea to **bank** is *compact-binary + background-thread saves* as the upgrade if SQLite
write-behind ever stalls the tick at scale. Low priority.

---

## 5. Networking & entity model — mostly aligned; one bandwidth lever
- **Outgoing compression (the one lever).** Mature servers compress *every* outgoing packet with a cheap
  stateless codec (a fixed Huffman table). Our snapshots are already compact binary, but **per-client
  bandwidth is our other named limiter at high visible density** — so compressing the outgoing snapshot
  stream (a fast codec like LZ4, or a fixed Huffman) is a real lever to squeeze the redundancy in 120–150
  small position records per tick. Worth a measured experiment when we push visible density. → candidate todo.
- **Per-tick send coalescing.** Accumulate all of a client's outgoing bytes for a tick and flush once,
  rather than many small sends. LiteNetLib likely already coalesces for us; verify before bothering. Minor.
- **Dirty-flag + delta property model.** The proven pattern — mark an entity dirty on change, send only the
  changed fields to viewers once per tick — is **what we already do** (`StateRevision` + incomplete
  snapshots that merge deltas). Aligned; no action.
- **Strongly-typed entity id.** A `readonly struct` wrapper around the id (vs a raw `uint`) is a small
  type-safety nicety. Trivial; only if we're touching that code anyway.

---

**Overall:** movement #1/#2 are the immediate feel wins; the **AOI spatial-index design is the standout**
architecture idea because it targets the scaling limiter we already identified; rendering is mostly free for
us as a 3D engine; persistence and the delta/entity model are already fine; and **outgoing-stream
compression** is the one networking lever worth a measured experiment against the per-client-bandwidth limit.
The study now spans movement, spatial index/AOI, 2.5D rendering, persistence, networking, and the entity
model — a comprehensive pass.
