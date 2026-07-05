# N — BOSS-2 review follow-ups (from the Fable review of fabf05d)

Both HIGHs (spawn-time plating broadcast dropped by the KnowsEntity gate; stale client plated-id
vs recycled network ids) were fixed immediately with the state-sync-at-EnsureEntitySpawns +
clear-on-despawn/prune pair. Remaining, none blocking:

- **Wire-integration pin for the plating state-sync (conscious gap).** The HIGH-1 fix lives in
  EnsureEntitySpawns and has no automated pin: the wire harness (TelegraphWireIntegrationTests)
  runs a small 64x64 test map, and /boss teleports to fixed REAL-map arena coordinates. When the
  harness gains a real-map variant, add: connect -> /boss -> assert BossPlating(true) arrives
  after the boss EntitySpawn, and again for a late joiner. Until then the pin is the live fight
  (an unplated-looking boss is visible in seconds).
- **LOW — combat.damage tunable to 1 makes duo-plated melee round to 0** (Round(0.25)=0 → silent
  no-op hit). Unreachable with defaults; floor plated damage at 1 if tuning play starts.
- **LOW — OnFusion has no participant check**: any pair fusing ANYWHERE shatters the arena boss's
  plating (engine gates only on encounter state). Moot at 2 players (they're always the arena
  pair); make it participants-only when the server ever hosts a second concurrent pair.
- **NIT — MessageType.cs stale comment**: MidpointCharge=126 still says "127 is the next free
  tag"; 127 is now BossPlating.
- **NIT — HandleAttack allocates a per-swing modifier closure**; cacheable in a field.
