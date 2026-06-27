# N — measure monster-dense bandwidth (Phase C per-tick monster replication)

From the Phase C independent review (commit `aac0fea`). Severity: Medium, but NOT currently exercised.

**What changed.** Phase C made a hopping slime a real ballistic Jump driven by the executor; the arc duration ==
the move cadence, so a roaming/chasing monster is `IsActive` essentially every tick → it is force-included for every
in-AOI viewer EACH TICK (`forceActionAirborne = IsActive`, GameServer snapshot build). Before Phase C a monster hop
was an instant teleport with `Velocity == 0` and no action, so it replicated only on a tile-cross StateRevision bump
(~once per hop). So per hopping-monster-per-viewer cost went from ~1/hop to ~1/tick — the deliberate trade for a real
replicated arc.

**Why it is NOT measured / not yet a live concern.** The 120/30s gate stress spawns only PLAYER bots; the default map
spawns NO monsters (they exist only via the admin `/monster` command + persistent spawners), so neither the stress nor
normal play currently exercises a monster-dense AOI. The players-only stress (79 visible, all moving per-tick after the
remote-walk fix) measured ~200 kbps/client with a healthy tick (4.49ms avg). Per-client downstream is bounded by the
AOI visible cap (`MaxVisibleEntities = 150`) × per-tick bytes (~17 B) × 20 Hz ≈ **~400 kbps/client worst case**, and
this bound is KIND-AGNOSTIC (a hopping monster costs the same per-tick as a moving player). So Phase C cannot exceed
the AOI-cap envelope the remote-walk fix already operates under — but the monster-dense worst case was not directly run.

**Do this WHEN** default monster spawners become part of the world (a future content task), OR sooner if you want the
number nailed:
- Add a way to populate N monsters inside the stress clients' AOI (e.g. an `MMO_MONSTER_SEED_COUNT` server option that
  seeds spawners near the clustered spawn, mirroring the existing `MMO_SPAWN_DISTRIBUTION`/world-size env the
  review-stress script already threads), then run the 120/30s gate with a dense monster population and confirm
  per-client downstream + tick budget stay within the parity envelope. If it bites, the lever is the same as the
  remote-walk follow-up: throttle a hopping monster's force-include to every-Nth tick (it is server-interpolated by
  viewers, so it tolerates a slightly sparser arc better than a predicted local player would).

Acceptance: a 120/30s stress with a realistic monster density in AOI shows per-client downstream and tick budget within
budget (0 errors), or the throttle above is applied + re-measured.
