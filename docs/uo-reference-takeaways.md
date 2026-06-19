# UO Reference Takeaways (ClassicUO client + ModernUO server)

Study of two mature UO codebases (read directly — ClassicUO cloned to `D:\UO-Project\client\classicuo`,
ModernUO at `D:\UO-Project\server\modernuo`) to adopt the best solutions **while keeping our Godot 3D
client + our held-intent C# server** — not a 1:1 port. Priority: movement. (S62.)

---

## 1. MOVEMENT — the priority (deep dive)

### How UO does it (verified from source)
- **Speeds** (`MovementSpeed.cs`): foot **walk 400 ms/tile, run 200**; mounted **200 / 100**. Run vs walk
  is chosen client-side by **cursor distance** (`GameSceneInputHandler`: run when cursor ≥ **190 px** from
  screen-center). No dead-zone — right-mouse-held moves immediately toward the cursor.
- **Turns are FREE.** (`WalkerManager.cs` header + ModernUO `MovementThrottle`: a direction change has
  `cost = 0`, flagged `DirectionChangeOnly`, NOT recorded in rate history.) A turn is sent + confirmed
  immediately and **does not consume the step cooldown**. The 80 ms `TURN_DELAY` is only a client input
  throttle, not a movement-blocking cost. You face the new direction instantly; the *next tile move* still
  fires at the normal cadence.
- **Per-move sequence + ack/reject** (client-driven pace). The client **predicts immediately**, sends a
  `0x02` walk request carrying a **sequence byte** (0–255 wrapping), and keeps up to **5 unconfirmed steps**
  (`MAX_STEP_COUNT`) in a `StepInfo[]` ring. Server replies `0x22` **ack** (echo sequence → mark Accepted,
  advance) or `0x21` **reject** → client `DenyWalk`: **clear steps, snap to the server's (x,y,z), resync
  facing**. Unknown sequence → full `Send_Resync`.
- **Server throttle = credit + queue** (`MovementThrottle.cs`, the canonical anti-speedhack). Each move has
  `cost = ComputeMovementSpeed(dir)`; `_nextMovementTime` gates it. Early packets are absorbed by a **credit
  buffer** (200 ms, +up to 150 ms RTT bonus for laggy players, can go into debt) — if credit covers it,
  execute; else queue (drains at the proper interval; hard cap 10 → reject+reset). Detection is **multi-
  signal**: rate ratio (target/actual; >1.05 suspicious, >1.10 definite), packet rate (mounted-run = 10/s,
  flag >12), **queue depth** (client caps unacked at 5 → server queue >4 ⇒ *modified client* ⇒ definite),
  RTT correlation (stable+fast ⇒ more suspicious; unstable ⇒ forgive lag bursts). Confidence-scored →
  **notify staff** (not auto-ban). RTT is measured via `ClientVersion` probes.

### Takeaways for US (prioritized)
1. **[DO NOW — the fix] Make turns FREE, not a full-cooldown step.** This is almost certainly why our
   movement "isn't quite there." Our S59 turn-then-move charges a **full ~150 ms step cooldown** for a turn;
   UO charges **zero**. Change: on a direction change, **update facing immediately (no cooldown consumed)**;
   the tile move then fires at the normal cadence. Whipping the mouse → instant free facing changes, you
   move when you settle — exactly the UO pivot. (Mirror it in `WorldEntity.TryStep` *and* the predictor.)
   → own todo.
2. **[HIGH — feel] Walk vs Run (two cadences), run-by-cursor-distance.** UO's responsiveness range comes
   from slow walk (400) + fast run (200), chosen by how far the cursor is from the player (≥190 px ⇒ run).
   We have one ~150 ms speed. Add a run cadence + the cursor-distance trigger (and/or a run key). → own todo.
3. **[VALIDATION — we're already better here] Our held-intent + server-paced model is anti-speedhack by
   design.** UO's whole elaborate `MovementThrottle` exists because UO is **client-driven** (the client
   requests each move, so the server must rate-limit + detect). **We are server-driven**: the client sends a
   held *direction*, and the **server** steps at its own cadence — a client physically cannot make itself
   move faster. So we do **not** need the credit/queue/detection machinery for movement speed. Keep our
   model; this is a real advantage to bank.
4. **[CONSIDER — prediction hardening] Explicit per-move reject vs our position-divergence guess.** UO
   reconciles on an explicit `0x21` reject (precise: "that exact step was denied → snap"). Our predictor
   *infers* divergence from snapshot position deltas (a 3-tile tolerance + snap). Ours is fine for a held-
   intent model, but if prediction ever needs hardening, the lesson is *explicit* server correction beats
   inference. Not needed now.
5. **[MINOR] RTT-scaled tolerance.** UO sizes its jitter credit by measured RTT (more slack for laggy
   players). Our reconcile uses a fixed 3-tile tolerance; scaling it by LiteNetLib's RTT would be a small,
   principled refinement.

**Net:** #1 (free turns) + #2 (walk/run) are the two changes that take our movement from "close" to "right";
#3 is a design win we already hold; #4/#5 are filed for later.

---

## 2. ARCHITECTURE — pending (next study phase)
Files identified, study not yet written up (the research subagents are sandboxed out of `D:\UO-Project` +
the web, so the Orchestrator reads these directly). To cover next:
- **ClassicUO client**: world-object model (`Game/GameObjects/` GameObject/Mobile/Item/Static), the
  isometric **depth-sort** (relevant to our 2.5D sprite/3D mixing), scene/input/UI split.
- **ModernUO server**: the **Sector** AOI model (`Maps/Map.cs` + the `*Enumerator` family — compare to our
  grid), the network pipeline + `Outgoing*Packets` batching, the fast **serialization/persistence**
  (`Serialization/`, `SerializationThreadWorker`, memory-mapped writer — vs our write-behind SQLite), and
  the timer-wheel loop (`Timer.TimerWheel.cs`).

Each takeaway that becomes a code change gets its own todo + commit.
