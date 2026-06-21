# S103 — Commit-step on release: finish a near-complete step (server-validated) instead of snapping back

Severity: S (movement feel — the user's main smoothness ask). Server + protocol + client. One cohesive,
revertable change. Protocol version BUMP (20 → 21, new client→server message); server+client ship together.

## Why / the behavior (agreed with the user)

In model B (cosmetic lead, default), the avatar's render glides ahead of the server's confirmed steps. If you
RELEASE the movement key when the glide is most of the way onto the NEXT tile but the server hasn't stepped
there yet, today it SNAPS BACK to the confirmed tile (jarring). Instead:

- **On release, if the lead progress is past a threshold** (tunable, default ~0.7 — "almost entirely on the next
  tile") **AND that next tile is walkable** (client-side `IsWalkableForPrediction`, S75 corner-cut rule):
  1. Do NOT snap back, and do NOT snap to the tile either. **Keep tweening to the next tile at NORMAL step speed**
     (finish the in-progress glide), and
  2. Send a new **commit-step request** to the server for that one step.
- **Accepted** (server steps there) → the avatar is already gliding/settled onto that tile; seamless, nothing
  else happens.
- **Rejected** (server does not step) → **snap back** to the confirmed tile, exactly like today.
- If progress is BELOW the threshold (or the tile isn't walkable), the existing release behavior applies
  (S102 `SnapOnRelease`: hard snap or soft settle). The commit-step is an ADDITIONAL release path, not a
  replacement.

The ONLY snap is on rejection; the accept path is a smooth completion of a step that was already ~70% done.

## Anti-cheat (the load-bearing design decision — preserves our no-speedhack property)

The glide reaches the threshold BEFORE the server's step cooldown fully elapses, so honoring the commit means
stepping a bit early. To keep that from being a speed exploit (a scripted client spamming release-commits):
- The server accepts a commit-step ONLY IF it is walkable AND the entity is **at least `CommitAcceptFraction`
  of the cooldown into the current step** (a server const, e.g. `0.5` — below the client's ~0.7 threshold so
  legit client commits are always accepted; a scripted commit below 50% is rejected).
- On accept, the server **sets `_nextEligibleTick = commitTick + stepCooldownTicks`** (a full cooldown from the
  commit) and bumps `StepSequence` exactly like a normal accepted step. So an early commit BORROWS from the next
  step's cooldown — the average step rate can never exceed the normal cadence. Net: you can finish a near-done
  step on release, but you cannot use commits to move faster. This keeps the held-intent model anti-speedhack.

## Implementation

### Protocol (20 → 21)
- New client→server `StepCommitRequestMessage(uint Sequence, Direction8 Direction)` in `Messages.cs` +
  `ProtocolCodec` write/read + a new message id; bump `ProtocolCodec.Version` to 21. Update `docs/protocol.md`
  (version + message list) in THIS unit of work. No new server→client message — the RESULT is observed via the
  normal snapshot stream (confirmed tile advances = accepted; stays = rejected).

### Server (`GameServer` + `WorldEntity`/`Zone`)
- Handle `StepCommitRequestMessage`: resolve the recipient entity; attempt a server-validated single step in
  `Direction` with the commit rule above. Add a `WorldEntity.TryCommitStep(direction, serverTick,
  stepCooldownTicks, acceptFraction, grid, out result)` (or extend `TryStep` with a commit mode) that:
  walkable-gates (same `IsStepWalkable`), enforces the `elapsed >= acceptFraction * cooldown` floor against the
  last step, and on accept advances the tile + `StepSequence` and sets `_nextEligibleTick = serverTick +
  stepCooldownTicks`. On reject, no state change (the next snapshot still shows the old tile). Stale-sequence
  guard like the move-intent path. `CommitAcceptFraction` is a server const (document it).

### Client (`MmoClient` + `LocalPlayerCosmetic`)
- `LocalPlayerCosmetic`: expose the current lead progress (0..1 toward `_leadTarget`). On `SetIntent(moving=false)`
  (release): if commit-step is enabled AND progress >= threshold AND `_leadTarget` is walkable (pass the oracle
  in, as the lead already uses it), enter a **pending-commit** state: keep the tween running to `_leadTarget` at
  the normal cadence (do NOT snap, do NOT apply the S102 release branch), and signal the client to send the
  commit. Otherwise fall through to the existing release behavior.
- `MmoClient`: on that signal, send `StepCommitRequestMessage(seq, direction)` (reliable-ordered). Track the
  pending commit (target tile + the step-seq it expects). On the confirming snapshots: if the confirmed tile
  reaches the pending target (accepted) → clear pending, leave the render (already there). If, after the commit
  has demonstrably been processed (the next snapshot(s) past the send, by RecipientStepSeq / a small bounded
  grace) the tile did NOT advance to the target → **rejected → snap back** to the confirmed tile and clear
  pending. **This accept/reject reconciliation timing is the highest-risk area — get the grace/ordering right so
  a not-yet-arrived accept isn't misread as a reject.**
- New `MmoClient.SetCommitStepOnRelease(bool enabled)` + `SetCommitStepThreshold(double)` (client-level, re-seeded
  on cosmetic attach like the other Cato/lead settings).

### F6 levers (add to the S102 panel)
- **"Commit step on release"** toggle (enable/disable; default ON to try it).
- **"Commit threshold (0..1)"** field (default ~0.7), clamped [0,1]. Both live, no restart.

## Tests
- **Server:** `TryCommitStep` accepts a walkable step past the accept-fraction (advances tile + StepSequence, sets
  next-eligible a full cooldown out) and rejects below the fraction / into a wall; a commit cannot raise the
  average step rate above cadence (step a held step, then spam commits — assert the tile advances no faster than
  cooldown allows).
- **Protocol:** `StepCommitRequestMessage` round-trips; version is 21.
- **Client:** on release past threshold the cosmetic render continues to the next tile (no snap) and a commit is
  emitted; an accepted confirm leaves it there; a rejected confirm (tile stays) snaps back. Below threshold,
  the S102 behavior is unchanged.
- Hardened `run-checks` green (now `--no-incremental`); Godot build clean; fresh **120/30s** stress (movement +
  new message path — confirm no regression and no error spam from commits).

## Constraints

- Server + protocol + client. Protocol bump is BREAKING — server+client rebuilt together; note it. **Safe Local
  Execution** binds you (scripts only; if a session locks `Mmo.Shared.dll`, stop via `stop-mmo.cmd`, note it).
  You cannot run Godot — the Orchestrator/human does the live check. If your shell is denied, say so explicitly
  and list exactly what must be run — do NOT claim green you didn't observe.
- Do NOT commit/push/delete the task file — leave the tree dirty + write
  `review/review-request-s103-commit-step-on-release.md`; the Orchestrator verifies and commits.

## Acceptance

- Releasing past the threshold finishes the step smoothly (tween at normal speed, no snap) and sends a
  server-validated commit; accept = stays, reject = snap back. Anti-cheat: average step rate stays capped at
  cadence (early commits borrow the next cooldown; sub-fraction commits rejected). F6 enable + threshold levers,
  live. Protocol 21. Clean-build run-checks green, Godot build clean, 120/30s stress clean. Review-request written.
