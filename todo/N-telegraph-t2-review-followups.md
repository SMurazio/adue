# N — Telegraph T2 review followups (independent review of fb08bb1: APPROVE-WITH-FOLLOWUPS)

No blockers/majors; codec symmetry, deadline-form sync, presentation-only clock, AOI diff, and Godot node
hygiene all verified sound. Surviving findings, priority order — (1) matters to the honest-telegraph
pillar and should land BEFORE/with the feel-test:

1. ~~Quantize the shape AT SCHEDULE~~ **DONE** — TelegraphScheduler.Schedule now quantizes origin+radius
   to the wire's Q12.4 grid (QuantizeToWire), pinned by a discriminating resolve test (victim inside the
   quantized circle, outside the raw one).

2. **Remember-known only on successful send (MINOR — fairness).** GameServer ~1414: the telegraph AOI
   diff calls RememberKnownTelegraph unconditionally, ignoring TrySend's bool. A failed send on a
   surviving session permanently marks the viewer as knowing a telegraph it never saw = hit with no
   warning. Remember only on TrySend == true (the diff naturally retries next tick). (Mirrors the
   SpawnerMarker pattern — consider fixing that one the same way while there.)

3. **Clear client telegraph state + clock on disconnect (MINOR — latent).** MmoClient.Disconnect leaves
   _activeTelegraphs + the cosmetic clock populated. Reconnect to a RESTARTED server (tick ~0): stale
   entries have resolveTick far in the future → never pruned → permanent ghost decal + dict entry.
   Latent for the Godot client (one MmoClient per process) but real for headless/tooling clients.

4. **Two negative-test gaps (MINOR).** (a) AOI exclusion: the wire test's small map puts everyone in
   AOI, so a scoping regression (telegraph broadcast to ALL) passes — add an out-of-range viewer
   asserting NO TelegraphMessage. (b) Server forget-on-resolve: nothing pins _knownTelegraphIds
   shrinking after resolve — a never-forgets regression is a slow per-session leak with no test.

NITs (batch opportunistically): one >2s latency spike snap-then-snap-backs the cosmetic clock (require
two consecutive out-of-band samples before re-anchoring); the flash re-assigns MaterialOverride every
frame of the 0.35s window (guard the redundant interop write).
