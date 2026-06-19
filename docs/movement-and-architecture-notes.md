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
- **Turns are free.** Changing facing is instant and does **not** consume a step's cooldown. You pivot in
  place immediately; the *next tile move* fires at the normal cadence.
- **Run vs walk by cursor distance** (mouse): hold-to-move toward the cursor, **run when the cursor is far**
  (~190 px from the player), walk when near. No dead-zone — movement starts immediately on hold.
- The client **predicts its own steps immediately**; the server is authoritative; on disagreement the client
  snaps to the server. A small bound on unconfirmed steps (~5) is plenty.

### Our takeaways (prioritized)
1. **[DO NOW — the fix] Make turns free, not a full-cooldown step.** This is almost certainly why our
   movement "isn't quite there." Our S59 turn-then-move charges a **full ~150 ms step cooldown** for a turn.
   Change: on a direction change, **update facing immediately (no cooldown consumed)**; the tile move then
   fires at the normal cadence. Whipping the mouse → instant free facing changes, you move when you settle.
   Mirror it in `WorldEntity.TryStep` **and** the predictor. → own todo.
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

**Overall:** movement #1/#2 are the immediate feel wins; the **AOI spatial-index design is the standout**
architecture idea because it targets the scaling limiter we already identified; rendering is mostly free for
us as a 3D engine; persistence is fine as-is. Remaining to mine if we want to go deeper: entity/component
organization and outgoing-packet batching.
