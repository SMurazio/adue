# N — Telegraph shapes review NITs (Fable review of 73fd58a, SHIP-WITH-FOLLOWUPS)

MEDIUM (lunge-type instant-dash fallback) and the west-aimed seam test were fixed at review time.
Remaining, none blocking:

- **Human feel-check owed (the one real gate):** the Godot decal MESH is headless-untestable — the
  render==hit fixpoint is pinned to the wire layer only. Live check: stand ON each drawn edge of the
  wedge and the line (side + far end), confirm hit/no-hit matches the drawing. A future yaw-sign flip
  in MmoClientRoot would ship silently without this.
- NIT — 24-segment wedge fan chord sags ~3mm inside the true arc at r2.8 (finer than the circle's
  accepted ~6mm class); bump segments if it ever reads unfair.
- NIT — a `slamShape` typo in monsters.json falls back silently to circle at cast (documented
  decision; the fallback is still an honest circle). Consider a load-time warning.
- NIT — NormalizePi is a private duplicate of FreeAimSector's; share it next time either changes.
