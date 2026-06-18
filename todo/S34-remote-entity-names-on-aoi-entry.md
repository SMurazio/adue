# S34 — Remote players show "#<id>" instead of their name

Severity: should-fix (visible correctness bug). Remote players render with a placeholder label
`#<networkId>` instead of their `DisplayName` (e.g. screenshots showed `#1`/`#2`/`#3` for players,
while the "Ancient Marker" NPC — spawned with a name — showed correctly).

## Cause

- Snapshots (`EntityStateSnapshot` = NetworkId, Tile, Facing) carry **no name**. Names come only from
  `EntitySpawn` (`DisplayName`).
- The client labels a snapshot-discovered entity `#<networkId>` (see `MmoClient.ApplySnapshot` →
  `UpsertEntity(..., $"#{state.NetworkId}", ...)`) and only upgrades to the real name when an
  `EntitySpawn` for it arrives.
- So the server likely sends `EntitySpawn` (with name) only at initial login, **not** when an
  already-connected player enters another player's AOI — leaving AOI-entry entities stuck at `#N`.

## Fix

In `GameServer` AOI/visibility handling: when an entity first becomes visible to a recipient (enters
that recipient's AOI), send an `EntitySpawn` (with `DisplayName`) to that recipient — not just at
login. Confirm despawn-on-AOI-exit + re-spawn-on-re-entry round-trips the name. Keep it within the
existing AOI hysteresis (don't spam spawn/despawn on the boundary).

## Acceptance

- A player entering another player's AOI shows their real `DisplayName`, not `#<id>`; leaving + re-
  entering re-applies it.
- No protocol change required (EntitySpawn already carries DisplayName); if one is needed, bump the
  protocol version per convention.
- `run-checks.cmd` green + a 120-client/60s stress (watch EntitySpawn/Despawn rates don't blow up).
