# S1 — Full-snapshot heartbeat can be starved, leaving stale entities under packet loss

Severity: should-fix (correctness; invisible on localhost, real over a lossy WAN)

## Problem

Changed-state snapshots mark an entity's revision as delivered on **transmit**, over an
**Unreliable** channel — so a lost update is never resent:

- `GameServer.BuildSnapshotPackets` calls `recipient.RememberSentRevision(session)` after sending
  (`src/Mmo.Server/Runtime/GameServer.cs:453`). `HasSentRevision` then suppresses resending that
  revision even though the packet may have been dropped.

The only recovery path is the periodic **full** snapshot, but that path is starved:

- `ClientSession.ShouldSendFullSnapshot` gates the heartbeat on `_lastSnapshotSentTick`
  (`src/Mmo.Server/Runtime/ClientSession.cs:111`).
- `RememberSnapshotSent` updates `_lastSnapshotSentTick` on **every** send, including partial ones
  (`src/Mmo.Server/Runtime/GameServer.cs:458`, setter at `ClientSession.cs:132`).

So in a continuously-active view, partial snapshots keep resetting the heartbeat clock and the full
resync rarely or never fires. Result: an entity that moved (update lost) then went idle can remain
at a stale tile on clients indefinitely.

## Fix

Track the last **full** snapshot tick separately so the heartbeat fires on schedule regardless of
partial activity:

- Add `_lastFullSnapshotSentTick` to `ClientSession`.
- `ShouldSendFullSnapshot` gates on `_lastFullSnapshotSentTick` (not `_lastSnapshotSentTick`).
- Update `_lastFullSnapshotSentTick` only when a snapshot with `isComplete == true` is actually
  sent. (Keep or remove `_lastSnapshotSentTick` depending on whether anything else needs it.)

This bounds staleness to one heartbeat interval (~1 s at 20 Hz) even under loss + activity.

Optional (note, do not implement here unless trivial): the stored but currently-unread
`LastAcknowledgedSnapshotSequence` is the proper long-term fix — only treat a revision as delivered
once the client acks a snapshot sequence at/after the one that carried it. Leave that for the future
delta-compression work (design plan D1); the separate full-snapshot clock is the minimal correct fix
now.

## Acceptance

- New regression test: with two clients continuously stepping (so partial snapshots flow every
  tick), a third observer still receives a `WorldSnapshot` with `IsComplete == true` within roughly
  one heartbeat interval + margin. (The existing `IntegrationClient` in
  `tests/Mmo.Server.Tests/AoiIntegrationTests.cs` already exposes `WorldSnapshotMessage` and can
  assert on `IsComplete`.)
- `run-checks.cmd` green.
