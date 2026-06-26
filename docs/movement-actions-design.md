# Movement-Action Framework — Design (DRAFT for review)

> Status: **design-first, not built.** This doc is for the user to review and refine before any
> production code. No code here is final; the phasing at the end is what gets implemented, one gated +
> reviewed phase at a time.
>
> Scope: a **reusable framework for movement actions** — jump, charge, dodge-roll, extensible — usable by
> **both players** (skill inputs) **and monsters** (AI; the slime's hop becomes a jump action).
>
> Locked decisions from the user (these are settled — the design serves them):
> 1. **CLIENT-PREDICTED** — an action feels instant for the local player, matching today's continuous
>    movement feel (zero-latency predict, deterministic reconcile, no rubberband on a clean network).
> 2. **DESIGN-FIRST** — this document precedes any building.
> 3. **JUMP = BALLISTIC Z-PHYSICS** — a jump is a REAL authoritative vertical motion (a replicated `Z`
>    coordinate that arcs up and lands), **not** a cosmetic height bump on the animation. Remote players
>    see the real height. This supersedes the slime's cosmetic `HopHeight` arc. (§1.4 is the full model.)
> 4. **CHARGE = LOCKED HEADING** — the heading is committed at trigger; the dash goes straight along it;
>    no mid-dash steering; it `SlideStop`s on a wall. (§1.2 / §1.5.)
> 5. **ACTIONS ARE ONE AT A TIME** — no queuing, no cancel-chaining. An action runs to completion or is
>    interrupted (stun/death/server cancel) before any next action can start. A trigger arriving while an
>    action owns movement is rejected, not buffered. (§2.5 / §2.8.)

---

## 0. Grounding: what exists today (read before designing)

This framework sits ON TOP of the just-migrated continuous movement. The relevant live machinery:

- **Server-authoritative continuous position.** `WorldEntity.Position` is a `WorldVector` (tile units,
  fractional). Players integrate per-input: `WorldEntity.ComputeMoveDelta(unitDir, dt)` sets `Velocity` +
  `Facing` and returns a raw delta; `Zone.IntegrateMovement` interposes swept-circle wall collision
  (`ContinuousCollision.Resolve` over `TileWalls.NeighborhoodWallsForMove`); `ApplyResolvedMove(newPos)`
  writes the collided position and bumps `StateRevision`/`StepSequence` **only on a rounded-tile crossing**
  (R1 — sub-tile moves do not bump, so snapshot cadence/bandwidth are unchanged).
- **Per-input wire.** `MoveIntentMessage(InputSeq, DirX, DirY, DtSeconds)` — one per render frame,
  unreliable-sequenced. The client owns dt; the server sanity-clamps it (`MaxMoveInputDtSeconds`) and
  debits a per-peer wall-clock **dt budget** (`ConsumeMoveDtBudget`) — anti-speedhack: integrated sim-time
  ≤ real elapsed + burst. The wire carries no magnitude/speed — the server owns speed.
- **Client prediction + reconcile** (`ContinuousPredictor`): every render frame, `PredictAndBuffer(dirX,
  dirY, dt)` integrates locally (zero latency) through the **shared** collision primitives and buffers the
  `{seq, dir, clamped-dt}`. On a snapshot, `Reconcile(serverPos, lastInputSeq)` re-bases to the server
  position, drops acked inputs, and **replays** the unacked buffer through the *same* integrator. Because
  the integrator is byte-identical client+server, **no loss ⇒ replay lands where prediction already was ⇒
  no correction** (the no-loss == no-correction contract, WITH collision). A correction smooths (decaying
  render offset) unless it exceeds `SnapThresholdUnits` (4u) → snap.
- **Two recently-fixed reconcile edges** the framework must not break:
  - **Sub-tile force-include**: the server force-includes a moving player's OWN entity in its own snapshot
    every tick while `Velocity != 0`, so the predictor reconciles against the *live* continuous position
    each tick, not the position frozen at the last tile crossing.
  - **Stop-edge re-publish**: `StopMovement()` bumps `StateRevision` once on the moving→stopped transition,
    so the precise stop position re-enters the under-loss self-healing path (UDP snapshots can drop the
    final moving frame).
- **Monsters** hop via `MonsterLocomotion.HopLocomotion`: a discrete collision-valid LEAP of
  `HopDistanceUnits` toward an AI-chosen target once per move-cadence (`TryBeginHop`/`IsHopReady` gate),
  **`Velocity` stays 0**. The AI (`MonsterRoamAi`) decides *where/when*; the locomotion decides *how*.
  Monsters do **not** predict — clients render them through `RemotePositionInterpolator` (a fixed-delay
  playout buffer that glides between received positions; it already absorbs the hop as a glide).
- **Attack** (`AttackMessage`) is the existing precedent for a **separate, predicted, server-validated
  action stream**: its own dedup cursor (`_lastAttackSeq`, never the move cursor — the NET6 "two streams,
  one cursor" lesson), a **client authored tick** so client+server compute an identical movement-root
  window under latency (`ApplyAttackMovementRootAuthored`), and the swing-root is the *same*
  `_nextEligibleTick` gate the predictor mirrors for free. **This is the template the action stream copies.**

> The single most important inherited principle: **determinism is the whole game.** Prediction only works
> because client and server run *byte-identical* trajectory code over the *same* inputs. The action
> framework lives or dies by extending that same shared-code discipline to action trajectories.

---

## 1. The action model

A **movement action** is a declarative definition of a **deterministic movement trajectory over a fixed
duration (in ticks)**, plus presentation (animation) and gameplay properties. It is *data* + a *shared
trajectory function*; triggering one starts a short-lived **action instance** that overrides/augments
normal movement integration for its duration.

### 1.1 The definition (`MovementActionDef`)

```
MovementActionDef
  Id              : ActionId         // stable byte/enum: Jump=1, Charge=2, DodgeRoll=3, … (wire + registry key)
  DurationTicks   : uint             // trajectory length in SERVER TICKS (fixed-point time, latency-independent)
  Trajectory      : trajectory-fn    // SHARED deterministic fn: (ctx, tickInAction) -> desired delta this tick
  Cooldown        : uint  (ticks)    // re-trigger gate; its own clock, NOT the move/attack cooldown
  Properties:
    Interruptible    : bool          // can damage/stun/another action cancel it mid-flight?
    CanSteer         : bool          // may the player change heading mid-action? (false = locked heading)
    IFrameTicks      : (start,end)   // tick window of invulnerability (empty = none)
    CollisionMode    : enum          // SlideStop (charge into wall) | PassThrough (rare) | Clamp
    Hitbox           : optional      // a moving hurt/contact box (charge body-checks; later combat hook)
  Vertical:                          // BALLISTIC-Z params (§1.4) — present only for jump-class actions
    JumpHeight      : double         // peak height in WORLD UNITS (apex of the arc); 0 ⇒ no vertical
    AirborneTicks   : uint           // ticks spent above the ground plane (derives v0/g; see §1.4)
    HorizontalMode  : enum           // ForwardArc (XY tracks heading×distance) | InPlace (straight up)
  Animation       : anim-id          // client visual; for jumps the animation is DRIVEN BY the real Z,
                                     //   not a separate cosmetic arc (the real Z replaces the hop-arc)
```

> **`DurationTicks` vs `AirborneTicks` for a jump:** they are the same span for a pure jump — the action
> lasts exactly as long as the entity is off the ground. `g`/`v0` are *derived* from `(JumpHeight,
> AirborneTicks)` (§1.4), so the def authors a height + a hang-time and the executor computes the constants.
> The client never supplies either — it supplies only a heading (§2.2).

The **trajectory function is the crux** and must be **pure + deterministic + shared** (lives in
`Mmo.Shared`, exactly like `ContinuousCollision`/`TileWalls`). Its signature:

```
WorldVector Trajectory(in ActionContext ctx, uint tickInAction)
  // returns the DESIRED world-space displacement for THIS one tick (pre-collision).
  // ctx is fixed at trigger time and never re-read from live state:
ActionContext
  Origin     : WorldVector   // GROUND-PLANE (XY) position at trigger (action tick 0)
  Heading    : WorldVector   // unit heading at trigger (or steered, if CanSteer)
  Target     : WorldVector   // for Jump: the landing XY (clamped server-side)
  Speed      : double        // the entity's SpeedUnitsPerSecond at trigger (or an action-specific speed)
  DtPerTick  : double        // FIXED 1/TickRate — actions are tick-quantised, NOT per-frame dt-driven
  GroundZ    : double        // the ground height at Origin (ALWAYS 0 today — the elevation HOOK, §1.4)
  Params     : action params // distance, JumpHeight, AirborneTicks, etc. (from the def / live tuning)
```

> Note the XY/Z split: `Origin`/`Target`/`Heading` are all **ground-plane** (the existing `WorldVector`).
> The vertical lives in `GroundZ` + the def's `JumpHeight`/`AirborneTicks` and is produced as a separate
> scalar per tick (§1.4). XY collision/AOI never see Z — they keep operating on the unchanged ground plane.

> **Key determinism decision:** an action runs on **fixed per-tick steps** (`DtPerTick = 1/TickRate`,
> `DurationTicks` total), **not** on the variable per-frame dt the ordinary movement uses. This is the
> single biggest divergence from normal movement and it is *deliberate*: a fixed tick-quantised trajectory
> is trivially reproducible byte-for-byte on client and server regardless of frame rate, sidestepping the
> dt-alignment subtlety that ordinary movement solves with the clamped-dt buffer. The client *predicts the
> action's ticks*, not its frames (it advances the action by whole server ticks using its own estimated
> server clock — the same `EstimateServerTick` the attack path already uses).

### 1.2 The three seed actions

| Action | Trajectory | Duration | Collision | Properties |
|---|---|---|---|---|
| **JUMP** | **BALLISTIC** (§1.4). A real authoritative vertical `z(t) = v0·t − ½·g·t²` arcs up and lands, **replicated** so remote players see the height. XY moves *forward along the locked heading* (`ForwardArc`) while Z arcs — XY per-tick delta = `Heading · Speed · DtPerTick`, **resolved by the shared XY collision** (you can still be wall-blocked horizontally; Z is free). Lands when `z` returns to `GroundZ` (0 today). | `AirborneTicks` (≈ hop cadence) | XY: `SlideStop` per tick (a jump into a wall stops horizontally but keeps arcing; you land short). Z: free, lands at `GroundZ` at the landing XY (the elevation HOOK). | not interruptible (airborne); locked heading; no i-frames (configurable); the slime's hop becomes a *real low* jump (its arc-animation replaced by the real Z). |
| **CHARGE** | DASH along the **LOCKED `Heading`** (committed at trigger, no steering) at a high action-`Speed`; per-tick delta = `Heading · Speed · DtPerTick`. **Stops early on collision** — `CollisionMode = SlideStop` halts it at a wall, ending the action (`SlideStop`, not slide-along: a charge into a wall *stops*, it does not slide). | up to N ticks (ends early on wall) | `SlideStop` (the normal swept-circle resolver) — a charge into a wall stops at it. | not interruptible (committed); **no steer (locked heading, locked decision #4)**; optional contact hitbox (body-check). |
| **DODGE-ROLL** | fixed-distance roll along `Heading` over a short N ticks; per-tick delta = eased distance curve. | short N | `SlideStop` | brief **server-authoritative i-frames** mid-roll (`IFrameTicks`); no steer; short cooldown; **one-at-a-time, never queued** (decision #5). |

**Adding a new action is cheap:** author a new `MovementActionDef` (a row in the registry) + a trajectory
function (often a one-liner over the shared math) + an animation. No netcode, no wire change, no executor
change — the executor is action-agnostic (it just calls `Trajectory` each tick).

### 1.3 The registry

A static, shared `MovementActionRegistry` maps `ActionId -> MovementActionDef`. Server and client load the
**same** registry (shared assembly). Tunable params (distances, durations, cooldowns) flow through the
existing `AdminSetTuning` live-tuning path under `action.<id>.<field>` keys and replicate via a small
`ActionTuningMessage` (mirrors `CombatTuningMessage`/`MonsterTuningMessage`) so client prediction and
server execution always use identical numbers — the same "kill client/server constant duplication"
discipline combat already follows.

### 1.4 The BALLISTIC-Z model (jump's real vertical — locked decision #3)

A jump is a **real authoritative vertical motion**, not a cosmetic lift. This is the main thing this design
fleshes out. It is deliberately modest in scope — it provides *the vertical coordinate + a deterministic
ballistic arc + the elevation hook* — and explicitly leaves "jump over a gap / onto a ledge" to a future
feature that needs world height data (called out below).

#### 1.4.1 A real vertical coordinate — `VerticalOffset`, separate from the ground plane

Entities gain an **authoritative, replicated vertical position**:

```
WorldEntity.VerticalOffset : double   // world units above the ground plane; 0 = on the ground, >0 = airborne
```

**Decision: a SEPARATE `double VerticalOffset` field, NOT a third component on `WorldVector`.** Rationale:

- `WorldEntity.Position` stays a 2-component `WorldVector` (XY, the ground plane). **All** existing
  collision (`ContinuousCollision`, swept-circle, `TileWalls`), AOI, distance/range, and snapshot-XY code
  is **unchanged** — they were never Z-aware and they don't need to be. The Z "rides alongside."
- The vertical is one cheap scalar, default `0`, **non-zero only while airborne** (a tiny fraction of the
  time). It does not touch the hot XY path.
- This cleanly separates the two collision domains: **XY collides** (walls), **Z is free** (no ceilings in a
  flat world). Making Z a `WorldVector` component would tempt the resolver to treat it as a third blocked
  axis, which is wrong.

`VerticalOffset` is **distinct from and supersedes** the existing cosmetic `HopHeight` (the slime's
render-only arc). Where the slime today fakes a hop arc in the client renderer, after this framework the
slime's hop is a *real* (low) ballistic jump and the cosmetic `HopHeight` arc is removed in favor of the
replicated `VerticalOffset` (§3).

#### 1.4.2 The deterministic ballistic trajectory

The vertical is a textbook projectile under constant gravity, **tick-quantised** so it is byte-reproducible:

- Fixed timestep `dt = 1/TickRate` (the same `DtPerTick` every action uses — never per-frame dt).
- Over `N = AirborneTicks` ticks, at integer tick `i` the elapsed action-time is `t = i · dt`:

  ```
  z(i) = GroundZ + v0·t − ½·g·t²          with t = i · dt
  ```

- **`v0` and `g` are DERIVED constants, not client-supplied.** Given the def's `JumpHeight` H (apex) and
  `AirborneTicks` N (total hang time `T = N · dt`):

  ```
  the arc returns to ground at t = T and peaks at the midpoint t = T/2 ⇒
       g  = 8·H / T²           (gravity that gives apex H at hang-time T)
       v0 = g · (T/2) = 4·H / T   (launch velocity)
  (derivation: apex H = v0²/(2g) with full-flight time T = 2·v0/g)
  ```

  These are pure functions of `(H, N, TickRate)` — fixed in the def/registry, identical on client and
  server. The client cannot inflate height or hang-time: it sends only a heading (§2.2).

- **Landing.** The entity is airborne for ticks `1..N`; at tick `N` the arithmetic returns `z = GroundZ`
  and the action ends, snapping `VerticalOffset` back to exactly `0` (no float drift at the seam — the
  executor sets it to the ground value explicitly on the landing tick, it doesn't rely on `z(N)` rounding).

Because every input to `z(i)` is an integer tick and fixed constants, the vertical path is **byte-identical
on client and server** — the same determinism contract as the XY trajectory (§2.3), now covering Z. Predict
== server for the height, with no packet-loss correction, exactly like XY.

#### 1.4.3 Horizontal motion during a jump — `ForwardArc` (default) vs `InPlace`

Two options; **the default is `ForwardArc`:**

- **`ForwardArc` (default, chosen).** While Z arcs, **XY advances forward along the locked heading** (jump
  distance ≈ `Speed · AirborneTicks · dt`, or an explicit def distance). This matches the slime's *leap*
  (which goes somewhere) and is the useful player case (jump forward over/onto things later). The XY per
  tick is an ordinary movement delta and **still goes through the shared swept-circle collision** — so an
  airborne entity **can still be wall-blocked horizontally**: it arcs up, hits a wall in XY, `SlideStop`s
  horizontally, and lands short at the wall. **Z is free** (it always completes its arc); only XY is
  constrained. This keeps "jump" honest in a walled world without any Z-collision machinery.
- **`InPlace` (alternative, available per-def).** XY holds at `Origin`; the entity jumps straight up and
  comes straight down. Useful for a "hop in place" telegraph or a vertical dodge. Selected via the def's
  `HorizontalMode`; costs nothing extra (the trajectory just yields a zero XY delta).

Per-def `HorizontalMode` means both are first-class without an executor change.

#### 1.4.4 Flat world now, elevation later — the ground-height HOOK

The world is **flat: ground height is 0 everywhere today.** A jump therefore returns to the plane. The model
is nonetheless written against a **ground-height hook** so elevation slots in later **without a redesign:**

- Landing resolves the vertical against **`GroundHeightAt(landingXY)`**, a shared function that **returns 0
  for every XY today.** The executor computes the landing-XY (where the forward arc ends), calls
  `GroundHeightAt`, and lands `VerticalOffset` to that value (always 0 now). `ActionContext.GroundZ` is the
  takeoff-side value (also 0 now).
- When real elevation arrives, `GroundHeightAt` reads world height data and the *same* executor lands the
  entity on a ledge or at the bottom of a drop — the ballistic math is unchanged; only the boundary
  condition (where `z` stops) moves.

> **Explicit scope boundary.** This framework provides the **vertical coordinate + the deterministic arc +
> the hook**. It does **NOT** provide "jump over a gap" or "jump onto a ledge" as gameplay — those need
> **world height data** (per-tile or per-region ground heights, gap/void tiles, ledge collision), which is a
> **separate future feature**. Today every jump lands back on the flat plane. The value delivered now is the
> real, replicated vertical (remote players see height; the slime hops for real) plus a clean seam so the
> elevation feature is additive, not a rewrite.

#### 1.4.5 Replication — `VerticalOffset` on the wire

The vertical must reach remote viewers (locked decision #3: remote players see the real height). Options +
choice:

- **Chosen: extend the entity snapshot state with `VerticalOffset`** (one value alongside the XY position),
  replicated the same way and on the same cadence as position. It is the natural home — height is per-entity
  authoritative state, exactly like XY.
- **Kept cheap.** `VerticalOffset` is **non-zero only while airborne**, so the common case is a constant 0.
  Encode it compactly (a quantised fixed-point height in a small int — heights are bounded by the max
  `JumpHeight`, so a `ushort`/scaled-byte easily covers the range) and, ideally, **only include it when
  non-zero** (a presence bit / "airborne" flag), so grounded entities pay ~nothing. The local player
  predicts its own Z (no extra inbound cost); only remote airborne entities carry the field.
- **Protocol implication.** This is a snapshot-state addition ⇒ a **protocol-version bump** and a codec
  change for the entity-state record (the same kind of change Phase B already makes for `ActionId`/the
  action stream — fold the vertical into that bump). Document it as: "entity snapshot gains an optional
  quantised `VerticalOffset`; absent ⇒ 0."
- **Derivable alternative (noted, not chosen as the primary):** since a remote viewer also learns the
  `ActionId` (jump) + its start tick, it could in principle *re-derive* Z from the same ballistic formula
  instead of replicating it. We **replicate the scalar** instead because it is robust under packet loss (a
  dropped action-start wouldn't strand a re-deriving client at the wrong height) and trivially cheap — but
  the derivation is the fallback if bandwidth ever bites.

#### 1.4.6 Airborne collision + interactions

- **XY wall-collision still applies** to a `ForwardArc` jump (§1.4.3): horizontal is resolved by the shared
  swept-circle resolver every tick; only the vertical is unconstrained.
- **Pass over/under other entities?** Moot today: there is **no entity-entity collision** in the sim
  (entities already overlap freely on the ground plane). So an airborne entity neither needs to "clear"
  others nor can it collide with them. (If entity-entity collision is ever added, the Z gives a natural,
  free "you cleared it if your `VerticalOffset` exceeds theirs" test — a latent benefit, not a current
  requirement.)
- **Facing during the jump: LOCKED.** Consistent with locked decision #4's spirit and the jump being
  non-interruptible/committed, facing is set from the launch heading and **held** for the airborne duration
  (the `ForwardArc` direction equals the launch heading anyway). No mid-air re-facing.
- **The slime refactor.** `HopLocomotion` triggers this **same** ballistic Jump (a real, low hop:
  small `JumpHeight`, short `AirborneTicks`, `ForwardArc` to its chosen landing). Its cosmetic arc-animation
  is **replaced by the real Z** — the client renders the slime at `VerticalOffset` instead of faking a hop
  curve. One Jump def, real vertical, drives both the player and the slime (§3).

---

## 2. Client-predicted netcode (the crux)

The action stream is a **sibling of the attack stream**, not the move stream: its own input message, its
own dedup cursor, its own authored tick. It layers a deterministic, tick-quantised trajectory **on top of**
the existing predict/reconcile loop without disturbing it.

### 2.1 The lifecycle (local player)

1. **Trigger (client).** Player presses a skill bound to `ActionId=Charge`. The client mints
   `actionSeq = ++_nextActionSeq` (a DEDICATED counter, like `_attackSeq`), snapshots an `ActionContext`
   (origin = current predicted position, heading = current aim/heading, speed = live speed, authored tick =
   `EstimateServerTick()`), and **starts a local predicted action instance**.
2. **Predict (client, instantly).** For each of the next `DurationTicks` server ticks, the local predictor
   runs the action: **the action's `Trajectory(ctx, tickInAction)` REPLACES the ordinary input integration
   for that tick** (the held-WASD direction is ignored while the action owns movement, unless `CanSteer`).
   The resulting per-tick delta goes through the **same shared collision resolver** as ordinary movement,
   and the result is buffered so reconcile can replay it (see 2.3). The avatar moves *immediately* — the
   charge dashes the instant the key is pressed, matching today's instant-movement feel.
3. **Send.** An `ActionIntentMessage(ActionSeq, ActionId, Heading/AimAngle, AuthoredTick)` goes on the wire
   alongside the per-frame `MoveIntent`. Sent **reliable-ordered** (like Attack): an action is low-rate and
   a dropped trigger must not be lost. The per-frame `MoveIntent`s keep flowing (the move stream is
   independent); while an action owns movement the server simply *ignores their motion* for the action's
   duration (see 2.4).
4. **Server validates + executes.** On receipt the server: dedups on `_lastActionSeq`; validates **can-act**
   (alive, not already in a non-interruptible action, off this action's cooldown, not rooted in a way that
   forbids it); for Jump resolves a collision-valid landing; arms the action's cooldown; and starts a
   server-side action instance that runs the **identical** `Trajectory` over the **same** `DurationTicks`,
   anchored at the **authored tick** (clamped to a sane window around the server tick, exactly like
   `ApplyAttackMovementRootAuthored`) so client and server cover the same logical tick span.
5. **Reconcile (deterministic ⇒ silent).** Each tick the server force-includes the moving own-entity
   (already true while `Velocity != 0`; an action sets/continues motion so this holds), and the predictor
   reconciles against the authoritative position. Because both sides ran the *same* trajectory over the
   *same* ticks through the *same* collision, **the predicted position equals the server position ⇒ zero
   correction** — exactly the no-loss == no-correction guarantee, now extended to actions.

### 2.2 The wire addition

One new client→server message, modeled on `AttackMessage`:

```
ActionIntentMessage(uint ActionSeq, byte ActionId, ushort Heading, uint AuthoredTick)   // reliable-ordered
```

- `ActionSeq` — DEDICATED monotonic counter, dedup'd on a DEDICATED server cursor `_lastActionSeq`. **Never
  shares the move or attack cursor** (NET6 lesson). 
- `ActionId` — the registry key.
- `Heading` — the quantized launch heading/target bearing (reuse the `AimAngle` ushort quantization; for
  Jump this encodes the target bearing, with the distance fixed by the def so the client can't fake reach).
- `AuthoredTick` — the client's `EstimateServerTick()` at trigger, so both sides anchor the trajectory to
  the same logical tick (the swing-commit-fix pattern, proven).

No server→client "action started" message is required for the *local* player (it predicted it) — and the
local player **predicts its own `VerticalOffset`** from the same ballistic formula, so no Z comes back to it
inbound. Remote viewers learn of the action through the existing snapshot position stream **plus** a small
replicated `ActionId` (animation) **plus** the replicated `VerticalOffset` (real height, §1.4.5).

> The wire still carries **only a heading**, never a height, a duration, a distance, or a Z. The jump's
> `JumpHeight`/`AirborneTicks` live in the server def; the client cannot inflate the arc (anti-cheat, §2.7).

### 2.3 The determinism contract (byte-identical client + server)

Same contract as movement, extended:

- **Shared trajectory code.** `Trajectory` functions live in `Mmo.Shared` (next to `ContinuousCollision`).
  Client predict, client replay, and server execute call the *identical* function. No client-only or
  server-only copy — ever.
- **Fixed tick stepping.** The action advances by whole server ticks at `DtPerTick = 1/TickRate`. The
  client predicts ticks using its estimated server clock; the server runs them on its real clock; both
  produce the same per-tick deltas because the only inputs are `(ctx, tickInAction)`, both fixed.
- **Shared collision.** Per-tick deltas resolve through the same `ContinuousCollision.Resolve` /
  `TileWalls` over the same blocked set + body radius (the radius the server replicated on `ServerHello`).
  A charge stops at the same wall on both sides.
- **Context fixed at trigger.** `ActionContext` is captured once and never re-read from live mutable state,
  so a divergence in live state mid-action can't desync the trajectory.
- **The vertical is in the contract too.** `VerticalOffset` is produced by the same shared ballistic
  formula (§1.4.2) over the same integer ticks and fixed derived constants, so the **Z path is
  byte-identical client+server** exactly like XY. The local player's predicted height equals the server's ⇒
  no Z correction under clean network. (Z has no collision today, so it can't even diverge via a
  resolver — it's the simplest part of the contract, but the determinism test must still assert it, §5/§6.)

When these hold, with no packet loss the **replay reproduces the server path byte-for-byte (XY and Z)** and
the reconcile is silent — identical to the movement guarantee.

### 2.4 Interaction with the existing predict/reconcile, dt-budget, the two fixes, collision

- **Replay (the buffer).** The reconcile replay must reproduce the action, so the predictor's unacked
  buffer gains an *action marker*: while an action is active, the buffered entries for those ticks carry
  `{ActionId, ctx, tickInAction}` instead of `{dir, dt}`. On replay, an action entry calls `Trajectory`
  (not the input integrator). This is a clean extension of the existing `BufferedInput` record — same
  replay loop, the entry just knows which integrator to call. The "drop acked ⇒ replay rest from base"
  logic is unchanged.
- **dt-budget / anti-speedhack.** An action does **not** consume the move dt-budget (it is tick-quantised,
  not client-dt-driven), so it can't be used to drain or dodge the budget. Its own per-action **cooldown**
  is the rate limiter, server-enforced. A charge can't be spammed to out-distance real time because its
  distance/duration are server-fixed by the def and gated by cooldown — there is no client-supplied
  magnitude or dt to inflate. (This is *stronger* than movement's budget: the client supplies only a
  heading, never a distance or a duration.)
- **Sub-tile force-include.** An action moves the entity (`Velocity` set, or position advanced each tick),
  so the moving own-entity force-include already re-publishes the live action position every tick — the
  predictor reconciles against the true in-flight action position, no special case needed. (If an action
  advances position via `ApplyResolvedMove` with `Velocity == 0` — like the hop does — the force-include
  predicate must also fire "while an action is active," a one-line extension of the `forceOwnWhileMoving`
  condition.)
- **Stop-edge re-publish.** When an action ends, the entity transitions to rest (or back to held-input
  movement). The existing stop-edge `StateRevision` bump must fire on the action→rest transition too, so
  the precise end-of-action position re-enters the under-loss self-healing path. (One extra place that
  calls the same `StopMovement` edge logic.)
- **Collision during an action.** Handled entirely by the shared resolver per-tick. A charge into a wall
  `SlideStop`s and the action ends early **identically** on client and server (same walls, same radius), so
  the early stop is itself deterministic and reconciles silently.

### 2.5 Interruption / cancellation (NOT queuing — see §2.8)

Interruption is the **only** way one action ends another. There is **no queuing and no cancel-chaining**
(locked decision #5): a running action finishes or is interrupted, and only *then* can the next start.

- `Interruptible` actions can be **interrupted** by a higher-priority *event* (stun, death, a server cancel)
  — not by another action trigger. The interrupt is a **server-authoritative event**: the server stops the
  action instance and re-publishes the position (and lands `VerticalOffset` to the ground if airborne). The
  client predicts a self-initiated cancel; a *server-initiated* interrupt (got stunned) arrives as state and
  reconciles like any correction (the predicted in-flight action diverges from the server's now-stopped
  position → a correction, smoothed or snapped by magnitude).
- Non-interruptible actions (jump airborne, committed charge) **ignore** interrupt inputs on both sides — so
  there's nothing to mispredict. A stun mid-jump is a design choice (default: let the airborne jump
  complete, since interrupting a real ballistic arc mid-air is visually jarring and needs a "fall" model).
- **An interrupt that lands an airborne entity** sets `VerticalOffset` to `GroundHeightAt(currentXY)` (0
  today) on the interrupt tick — a clean, deterministic ground-snap, the same boundary the normal landing
  uses.

### 2.6 A mispredicted / rejected action (the unhappy path)

If the server **denies** the action (on cooldown the client mis-estimated, dead, already mid-action,
anti-cheat reject), it simply **does not run the action** — it keeps integrating ordinary movement. The
client, which *did* predict the action, now diverges from the authoritative position. The standard
reconcile handles it: the server position (no action ran) differs from the predicted (action ran) → a
correction. If small, the render offset decays it smoothly; if it exceeds `SnapThresholdUnits`, it snaps.
**No new rollback machinery** — a rejected action is just a large-ish mispredict the existing reconcile
already knows how to absorb. (Optionally, a tiny owner-only `ActionRejectedMessage(ActionSeq, reason)` lets
the client cancel its local action *early* instead of waiting for the position reconcile, tightening the
visual; this is a polish nicety, not required for correctness.)

### 2.7 Anti-cheat (a client can't fake i-frames or teleport)

- **The client supplies only `(ActionId, Heading, AuthoredTick)`** — never a distance, duration, position,
  or i-frame window. All of those come from the **server-side def**. So a client cannot charge farther,
  roll longer, or teleport via a forged trajectory.
- **i-frames are server-authoritative.** Invulnerability during a dodge-roll is applied by the server from
  the def's `IFrameTicks`, anchored at the authored tick. The client *renders* the i-frame state but the
  server *decides* it (damage resolution checks the server's action state). A client claiming i-frames it
  doesn't have changes nothing — the server never sees the claim.
- **Jump target is server-clamped** to a collision-valid landing within the def's fixed distance — a client
  can't jump through a wall or across the map.
- **Cooldown is server-enforced** on `_lastActionSeq` + the per-action cooldown clock; spamming triggers
  are rejected (and reconciled away, §2.6).
- **AuthoredTick is window-clamped** (`[serverTick - past, serverTick + future]`) exactly like the swing
  path, so a forged tick can neither root the player far in the future nor dodge a penalty in the past.
- **`VerticalOffset` is server-produced**, never client-sent. A client cannot claim a height it doesn't have
  (to "look like it cleared something" once entity-Z interactions exist) — the server owns the scalar.

### 2.8 One action at a time (locked decision #5)

Actions are **strictly serial**: at most one action instance per entity, ever. This is a deliberate
simplification that removes a whole class of misprediction.

- **The server enforces it.** `can-act` validation (§2.1 step 4) rejects an `ActionIntent` whose entity is
  **already running a non-interrupted action**. There is **no queue** — the rejected trigger is dropped, not
  buffered for later. The only ways the current action ends are: it completes its `DurationTicks`, or it is
  interrupted by an event (§2.5).
- **The client mirrors it.** While the local predictor has an active action instance, it **ignores new
  action inputs** (it does not start a second predicted action). So the client never predicts a chain the
  server will reject — the common "spam two skills" case produces *no* mispredict at all, because the client
  declined the second trigger locally just as the server would.
- **No cancel-chaining.** You cannot cancel a charge *into* a roll, or buffer a jump to fire on landing. The
  player must wait for the action to finish (or be interrupted) before the next. (Cooldowns gate re-triggers
  on top of this.)
- **Why:** queuing/cancel-chaining multiplies the predicted-state space and the reconcile edge cases
  (what does the client do when the server accepts action 1 but rejects queued action 2 mid-flight?).
  One-at-a-time keeps the predicted timeline a single linear action span — far easier to make deterministic
  and to reason about under loss/latency. Revisit only if a design demands combos.

---

## 3. Reusability — one definition drives players AND monsters

The **same server-side action executor** runs a player-triggered action and a monster-AI-triggered action.
The difference is only *who triggers* and *who predicts*:

```
                       MovementActionDef (shared registry)  ──┐
                                                              │ identical Trajectory + props
   PLAYER skill input ──► ActionIntent (wire) ──►  ServerActionExecutor.Start(entity, def, ctx)
                                                              ▲           │ runs Trajectory per tick,
   MONSTER AI         ──► MonsterAi.TriggerAction(def) ───────┘           │ resolves collision, applies
                                                                          ▼  position, ends on done/wall
                                                            WorldEntity.Position advances authoritatively
```

- **The executor is the single source of truth.** `ServerActionExecutor` holds the per-entity active action
  instance and advances it each server tick (call it from the tick loop, next to `StepMonsterAi` and the
  player integration). It is **entity-agnostic** — it doesn't care if the entity is a player or a monster.
- **Player path:** `HandleActionIntent` validates + calls `executor.Start(playerEntity, def, ctxFromWire)`.
  The player **predicts** it (§2).
- **Monster path:** the AI chooses an action and calls `executor.Start(monsterEntity, def, ctxFromAi)`. The
  monster does **NOT** predict (it's remote-interpolated) — the action runs server-side only, clients
  **animate** it from the replicated `ActionId`, and clients **render the real height** from the replicated
  `VerticalOffset` (the `RemotePositionInterpolator` already glides the monster's XY between the per-tick
  action positions; the Z rides alongside as a replicated scalar, §1.4.5).
- **`HopLocomotion` becomes "trigger a real ballistic Jump."** The slime's hop is exactly a Jump: a discrete
  forward leap to a collision-valid landing over a short duration — **now with a real, replicated vertical**
  (a low `JumpHeight`, short `AirborneTicks`, `ForwardArc` to its chosen landing). Refactor
  `HopLocomotion.Advance` so that, when the hop cadence is ready and the AI has a target, it computes the
  same collision-valid landing XY it does today and calls `executor.Start(monster, JumpDef, ctx{origin,
  target=landing, …})`. The executor's `ForwardArc` reproduces the XY leap and the ballistic Z replaces the
  old **cosmetic hop-arc animation** — the slime now *really* rises and lands. The existing `TryBeginHop`
  cadence gate becomes the Jump cooldown; the fan/livelock logic (choosing *where* to jump) stays in the
  AI/locomotion — only the *how it moves there* is handed to the shared executor. **The cosmetic `HopHeight`
  render arc is removed** in favor of the replicated `VerticalOffset`. **One Jump definition — with a real
  vertical — now drives both** the player's jump skill and the slime's hop.

> Why this is safe for monsters: they already don't predict, and the hop is already a discrete
> collision-valid leap. Re-expressing it as a real ballistic "Jump action" changes the *plumbing* (it now
> goes through the shared executor) and **upgrades the cosmetic arc to a real replicated `VerticalOffset`**,
> without changing the *XY geometry* (same landing resolve, same radius, same cadence — the slime lands in
> the same place, just having really risen on the way). The Phase-8 livelock fixes (OnCooldown vs Stuck, the
> fan) are preserved because the AI still owns target selection and the watchdog.

A small replicated **`ActionId` on the entity snapshot** (or a one-shot `ActionStartedMessage` to AOI
viewers, like `DamageEvent`) tells remote clients which animation to play, **alongside the replicated
`VerticalOffset` that gives the real height** (§1.4.5). For a monster the client plays the jump animation
*at the replicated height* while the interpolator glides its XY through the received positions; for a remote
*player* doing a jump/charge/roll, the same mechanism drives the right animation (and, for a jump, the real
arc height) on other screens. This keeps the **animation choice client-side and presentation-only** while
both the **XY trajectory and the vertical stay server-authoritative**.

---

## 4. Integration with the now-good continuous movement

- **An action overrides normal movement for its duration.** While `executor` has an active action for an
  entity, the per-input movement integration (`HandleMoveIntent` → `Zone.IntegrateMovement`) is **suppressed
  for that entity** — the action owns its position. Held `MoveIntent`s still ACK (so the move cursor
  advances and the buffer trims), they just produce no motion (mirroring exactly how a rooted player's input
  "ACKs but produces zero motion" today). When the action ends, ordinary movement resumes from the action's
  end position.
- **Collision during an action coexists with the reconcile.** The action's per-tick deltas use the same
  resolver/walls/radius as movement, so a charge stopping at a wall is deterministic and reconciles silently
  — no special reconcile path. The action never bypasses collision (except an explicit `PassThrough` def,
  which would still be deterministic).
- **The tile-gated re-send fix is preserved.** The action advances `Position`; `ApplyResolvedMove` still
  bumps `StateRevision`/`StepSequence` only on a rounded-tile crossing (R1), so snapshot bandwidth is
  unchanged for sub-tile action ticks. The moving own-entity force-include re-publishes the live action
  position every tick (extend the predicate to "moving **or** action-active" if an action runs with
  `Velocity == 0`, like the hop). The stop-edge re-publish fires on the action→rest transition.
- **Facing during an action.** Set from the action heading at trigger and **held** for the locked-heading
  actions (jump, charge — locked decisions #3/#4) and for any non-steerable action. (No `CanSteer` action
  ships in the seed set; the field stays in the def for future curved actions, where facing would track the
  steered heading.) The same `SetFacingFromUnit` / `ComputeMoveDelta` facing table is reused so facing stays
  consistent with ordinary movement and replicates identically.
- **The vertical (`VerticalOffset`) rides alongside the XY integration.** A jumping entity's XY advances
  through the normal resolver (`ForwardArc`, §1.4.3) while its `VerticalOffset` follows the ballistic arc;
  on landing/interrupt it snaps to `GroundHeightAt(landingXY)` (0 today). XY collision/AOI/snapshot-XY are
  untouched — the Z is purely additive replicated state. The tile-gated re-send (R1) still governs the XY
  `StateRevision` bump; the airborne `VerticalOffset` updates piggyback on the moving own-entity / remote
  snapshot the entity already emits each airborne tick (it is moving, so it is force-included anyway).
- **Speed-multiplier / retune.** The action reads `Speed` into its `ctx` at trigger (a snapshot), so a live
  speed retune mid-action is intentionally ignored for that action's duration (consistent with the
  fixed-context determinism rule). The next action picks up the new speed.

---

## 5. Risks + open questions

Movement netcode is **high-risk** (the project's three historical misses — UO5-stall, NET2, NET3-live — all
passed an author-written headless test that inherited the same wrong model). Treat every phase with full
rigor: measure/repro first, **independent review**, headless determinism repro before any fix, green gate
before merge.

**Risks**

1. **Determinism drift (the cardinal risk).** Any client/server divergence in the trajectory or collision
   desyncs every action. Mitigation: trajectory + collision are shared-assembly code with **no second copy**;
   a headless determinism test runs the same `Trajectory` + resolver on "client" and "server" instances over
   the same `(ctx, ticks)` and asserts byte-identical paths (and asserts it does so *with* a wall in the
   path). This test must reproduce the *live* symptom (an action under loss/latency), not just the happy path
   — the explicit lesson from the three misses.
2. **Destabilizing the now-good movement.** The action layer must not regress the sub-tile force-include or
   stop-edge fixes. Mitigation: the action reuses those exact mechanisms rather than adding parallel ones;
   the existing movement reconcile tests stay green; Phase A/B land the action layer *additively*.
3. **Tick alignment under latency.** The action spans `DurationTicks`; client and server must cover the same
   logical span. Mitigation: the authored-tick anchor (proven by the swing-commit-fix) + window clamp.
4. **i-frame authority + timing.** A roll's i-frames must align between the client's *visual* and the
   server's *damage* resolution, or a player "looks invulnerable but takes the hit" (or vice-versa).
   Mitigation: i-frames are purely server-authoritative for damage; the client visual is cosmetic and
   anchored to the same authored tick. *Open question:* how tight must the visual/authoritative i-frame
   alignment be to feel fair under 100–150ms latency? Needs a live feel-test (human-only).
5. **One-at-a-time enforcement (decision #5).** The risk is no longer "what queuing model" (settled: none)
   but **enforcing serial actions cleanly** on both sides so a spammed second trigger never mispredicts.
   Mitigation: server `can-act` rejects a trigger while an action is active *and* the client declines a
   second local action (§2.8) — the rejected-trigger reconcile (§2.6) is the safety net if they ever
   disagree.
6. **Charge vs. moving entities / hitbox.** A charge body-checking another entity touches combat. Keep the
   first cut **movement-only** (no contact damage); the hitbox is a later combat hook.
7. **The new vertical coordinate (`VerticalOffset`).** Adding authoritative per-entity Z touches the entity
   state, the snapshot codec, and the renderer. Risk: a subtle desync between predicted and replicated
   height, or the field leaking into XY collision/AOI. Mitigation: Z is a **separate scalar** that XY code
   never reads (§1.4.1); it is produced by the **shared** ballistic formula (determinism contract, §2.3); a
   determinism test asserts the Z path byte-for-byte; the renderer reads it purely for presentation.
8. **Replicating the vertical.** A snapshot-state addition ⇒ protocol bump + codec change; get the "absent ⇒
   0 / only-when-airborne" encoding right so grounded entities pay nothing and a dropped airborne snapshot
   self-heals (the replicated scalar is loss-robust vs. re-deriving from action-start, §1.4.5). Mitigation:
   fold the vertical into Phase B's protocol bump; reuse the existing snapshot self-heal path.
9. **The airborne XY/Z model.** `ForwardArc` lets an airborne entity be wall-blocked in XY while Z is free
   (§1.4.3) — the early-stop-then-land must be deterministic on both sides. Mitigation: XY uses the *same*
   shared resolver as ground movement (so the early stop is already a solved, deterministic case); Z has no
   collision so it can't diverge. The determinism test covers "jump into a wall, land short."
10. **Keeping ballistic determinism under loss/latency.** The whole jump (XY arc + Z arc) must replkončay
    byte-identically when inputs are lost or arrive late. Mitigation: the trajectory is tick-quantised on the
    authored tick (§2.3); the determinism test runs the **full ballistic jump** (forward arc + Z, with a
    wall, under simulated loss/latency) and asserts predict == server — this is the test that must reproduce
    the *live* symptom, the explicit lesson from the three historical misses.

**Open questions for the user** (the jump-model and steering questions are now SETTLED — see locked decisions)

- ~~Ballistic Z vs. cosmetic height?~~ **SETTLED: ballistic Z** (decision #3, §1.4).
- ~~Charge steering — locked or curve?~~ **SETTLED: locked heading** (decision #4).
- ~~Queuing / cancel-chaining?~~ **SETTLED: one-at-a-time, no queue** (decision #5, §2.8).
- **Jump distance:** should `ForwardArc` distance derive from `Speed × AirborneTicks`, or be an explicit
  per-def distance (decoupled from move speed)? *Recommended:* explicit per-def distance, so a jump's reach
  is tuned independently of walk speed.
- **Stun mid-jump:** let the airborne jump complete (default), or add a "knocked out of the air → fall"
  model? *Recommended:* complete it for the first cut (no fall model); revisit with combat.
- **i-frame fairness under latency** (dodge-roll): how tight must visual↔authoritative i-frame alignment
  feel under 100–150ms? Needs a live human feel-test (Phase E).
- Do monsters ever need *predicted* actions? *Recommended:* no — they're interpolated; keep them server-run.

---

## 6. Phased implementation plan (each phase independently gated + reviewed)

Each phase is its own branch off `main`, its own gated + independently-reviewed unit of work, and de-risks
the next. The phasing front-loads the determinism risk and proves reuse before adding more actions.

**Phase A — Action model + the vertical coordinate + server-side BALLISTIC-JUMP executor (no netcode yet).**
Build `MovementActionDef` (incl. the `Vertical` params), the registry, `ActionContext` (incl. `GroundZ`),
the shared `Trajectory` interface, the shared **ballistic-Z formula** (§1.4.2) + the `GroundHeightAt` hook
(returns 0), and `ServerActionExecutor` (start/advance/end per tick). **Add the authoritative
`WorldEntity.VerticalOffset` field** (default 0). Implement JUMP's trajectory — `ForwardArc` XY through the
shared resolver **plus** the ballistic Z driving `VerticalOffset` — and a server-only path to trigger it (a
dev/admin command or a test harness — no wire). Gate: the executor runs a ballistic jump, advances XY
through the shared resolver while `VerticalOffset` arcs up and lands back to 0, ends correctly; a headless
test asserts **both** the XY per-tick path **and the Z trajectory** (apex height, landing tick, ground-snap)
and the "jump into a wall → land short" case. **De-risks:** the action model, the new vertical coordinate,
and the executor in isolation, with zero netcode in play.

**Phase B — Client prediction + reconcile + the wire + replicate the vertical.**
Add `ActionIntentMessage` (+ codec, + protocol-version bump, + `_lastActionSeq` dedup) **and the replicated
`VerticalOffset` in the entity snapshot state** (codec change folded into the same protocol bump; the
"absent ⇒ 0 / only-when-airborne" encoding, §1.4.5). Extend the predictor's buffer to carry action entries,
**predict the action locally including the ballistic Z arc**, send the intent (heading only), and reconcile
(XY and Z). Enforce **one-at-a-time** on both sides (§2.8). Validate server-side (can-act incl. "no action
already active", cooldown, authored-tick clamp). Gate: the **byte-identical client/server trajectory
determinism test — covering the full ballistic jump (forward XY arc + Z arc), with a wall in the path, under
simulated loss/latency** — plus a rejected-action reconcile test and a spammed-second-trigger (one-at-a-time)
test. **De-risks:** the netcode crux — the no-loss == no-correction guarantee extended to actions *and the
vertical*, the replicated-height path, and the rejected-action snap-back. This is the highest-risk phase;
full rigor, independent review.

**Phase C — Reuse for the slime (refactor `HopLocomotion` → real ballistic Jump action).**
Re-express the slime's hop as `executor.Start(monster, JumpDef, …)` with a low `JumpHeight` + short
`AirborneTicks`; **the cosmetic `HopHeight` render-arc is removed in favor of the replicated real
`VerticalOffset`** (the slime now really rises and lands); the cadence gate becomes the Jump cooldown. Keep
the AI's target selection + fan + livelock watchdog. Add the replicated `ActionId` (or `ActionStarted`
message) so clients play the jump animation at the replicated height. Gate: the existing monster
locomotion/livelock tests stay green; a stress run shows monsters hopping unchanged in XY (now with real
height); remote clients render the jump arc from `VerticalOffset`. **De-risks:** the player↔monster reuse
claim — proving one real-vertical Jump definition drives both before building more actions on top.

**Phase D — Charge + dodge-roll.**
Add the two definitions + their trajectories + collision modes (`SlideStop` early-stop for charge) +
dodge-roll i-frames (server-authoritative). Each is a new def + trajectory + animation — no executor/netcode
change (that's the payoff of A–C). Gate: determinism tests per action; charge-into-wall early-stop is
deterministic; i-frame damage resolution is server-authoritative. **De-risks:** the "adding an action is
cheap" claim, and the i-frame authority model.

**Phase E — Skill-input wiring + animations.**
Bind player skill inputs (hotkeys/skill bar) to action triggers, wire the client animations for jump/charge/
roll (the jump animation **driven by the real replicated `VerticalOffset`**, not a faked arc), and the
cosmetic polish (landing dust/squash, roll dust, optional early `ActionRejected` cancel). Gate: live
feel-test (human-only) under latency for i-frame fairness, jump responsiveness, and that a remote player's
jump height reads correctly on another screen. **De-risks:** the player-facing feel + the live netcode
behavior (incl. the replicated vertical) that only a human can judge.

> Sequencing rationale: A proves the model headless; B proves the netcode crux (the riskiest thing) with the
> simplest action; C proves reuse before the framework grows; D shows new actions are cheap; E is the
> player-facing layer that needs human feel-testing. Each phase is revertable and independently reviewed, per
> the project's branch + review-independence discipline.
