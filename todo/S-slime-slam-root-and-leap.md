# S — Slime slam: ROOT during windup + LEAP to the telegraph center at resolve (user feel-test feedback)

User feel-test verdict on T1+T2 (2026-07-03): "the tech seems to work", but the slime's slam reads wrong:
it can move and keep meleeing while the telegraph charges. Redesign the slam into the slime's signature
move:

1. **ROOT while channeling.** From cast to resolve the slime does not move, hop, chase, or melee —
   telegraphs must be honest ([[combat-pillar-fair-and-responsive]]): the wound-up danger is a committed
   action, not a free extra. Use the existing movement-freeze seam (the windup-root the T1 review + T3
   already flagged). (A channel animation slots in here when ART lands one.)
2. **LEAP into the telegraph center at resolve.** When the windup completes, the slime jumps to the
   circle's center — physically delivering the slam it advertised. Reuse the existing slime hop/ballistic
   locomotion (HopDistance/Height/AirborneMs machinery) aimed at the LOCKED telegraph origin; time it so
   the slime LANDS at (or a tick before) the resolve tick — the landing IS the hit. The leap is cosmetic
   truth-telling: resolve stays TelegraphScheduler's (positions at T, center-point membership, unchanged).
   Decide + document the edge: leap distance beyond hop range (clamp the CAST trigger range so the leap
   is always reachable, preferring trigger-range ≈ hop-range).
3. **Cast origin stays the TARGET's position at cast time** (dodgeable), so the slime visibly commits to
   where you WERE — stepping out and watching it crash down behind you is the fantasy.
4. Tests: rooted-during-windup pin (no position change / no melee between cast and resolve), leap-landing
   pin (slime at/near origin after resolve), cadence + cooldown pins still green.

This is the slime slice of [[N-telegraph-T3-content-and-tuning]] pulled forward; gnoll second pattern +
knob tuning stay there. High-risk band (server behavior + combat feel): implementer + gates + independent
review.
