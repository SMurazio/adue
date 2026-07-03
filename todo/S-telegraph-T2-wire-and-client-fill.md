# S — Telegraph arc T2: the wire event + client ground-decal fill on the synced deadline

Depends on T1 (`S-telegraph-T1-server-engine`). The deadline-form sync from docs/ability-telegraph-sync-design.md:
clients count down to a SHARED resolve tick, so caster-long/observer-short latency compensation falls out for
free and late AOI joiners render the correct remaining fill.

## Scope

1. **Protocol (v43 → v44, update docs/protocol.md in the same commit — the drift test enforces it):**
   `TelegraphMessage {telegraphId, shape type + params (origin Q12.4, radius), startTick, resolveTick}`,
   reliable, AOI-scoped at schedule time; on AOI-ENTER mid-windup the viewer receives the still-active
   telegraphs (the SpawnerMarker late-join pattern; telegraphs are short-lived so the active set is tiny).
   A resolve/cancel notice is NOT needed: clients self-resolve at T (the whole point); caster death mid-windup
   → follow T1's decided semantics (if T1 drops the telegraph, send a cancel — keep the wire minimal).
2. **Client server-clock offset (COSMETIC ONLY):** estimate `serverTick ≈ f(localClock)` from the serverTick
   already riding every snapshot header, smoothed (EMA over arrivals). Used ONLY to drive telegraph fill
   progress `(now − start)/(T − start)` — never for simulation/prediction (the B2 lesson: no EstimateServerTick
   in the sim; this is presentation).
3. **Ground-decal rendering (Godot):** flat circle on the ground at the telegraph origin, radial/alpha fill by
   progress, resolve flash at T, then despawn. Follow the existing ground-marker precedent (spawner tiles /
   prediction markers). Readable at the game's zoom — this is pillar-2 feel work; keep it simple + legible
   (no shader art pass yet).
4. **NOT in scope:** player-cast telegraphs, cone/line rendering (build the decal seam shape-generic, render
   circle), any change to resolve authority (server-only, T1).

## Acceptance criteria

- Codec round-trip tests (v44); protocol.md updated (drift test green).
- Headless: late-AOI-join receives active telegraphs; the client-side fill math hits 1.0 at (estimated) T; the
  offset estimator converges under jittered snapshot arrivals (extend the smoothness-harness style).
- HUMAN feel-test: the fill visually completes AT the hit on both a close and a (simulated-latency) far client;
  dodging out of the circle before the fill completes = no damage.
- Gate + godot-build green; independent review (protocol + sync model — full rigor).
