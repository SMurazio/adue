# N — monster AI dormancy when unseen (scaling design goal)

**Design decision** (see `docs/living-enemies-design.md` → "AI dormancy when unseen"). Make monster AI go
**dormant — skip the per-tick brain — when no player is within its AOI**, so off-screen monsters cost ~zero
server CPU (matching the AOI-gated replication model). This is what keeps total monster count from scaling down
player capacity: a densely populated world should cost the same baseline as an empty one.

## Trigger (when it makes sense — NOT now)
At low monster counts the always-on AI is negligible; per *measure before optimizing*, implement when:
- **P3 spawners** start populating the world with many monsters, OR
- a stress/profile run shows the per-tick monster-AI cost (the `StepMonsterAi` pass) is material.

## How
- Gate `StepMonsterAi` per monster on "is any player within this monster's AOI (or aggro+leash radius)?" — skip
  its update entirely if not. The aggro scan already locates nearby players, so this is cheap.
- Combine with the **monster-only index** (the P1-review nit: `StepMonsterAi` scans ALL entities each tick) so
  the per-tick O(entities) sweep also goes away — together they make baseline load ~independent of monster
  count.
- Verify with a stress run: N idle monsters far from any player should add ~0 to tickMs.
