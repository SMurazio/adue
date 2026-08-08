# N — Fusion-fix review followups (minor, from the d1fb411 review)

1. **Test backfill:** (a) near-boundary earned-flight case — the look-ahead window classifies
   up to 2.4u before the actual crossing, so the effective bar is ~2.0u + look-ahead; pin a
   head-on cross in the 2-3u band (currently degrades to Solo — stricter than documented,
   conservative direction). (b) staggered-fire rule: one well-traveled + one fresh projectile
   → Solo (BOTH must clear, `SkillshotEngine.cs:~271`). (c) cooldown quantization: derive
   `_fireCooldownTicks` from 0.6s x TickRate in the test instead of hardcoding 12, pinning
   `Max(1, Round(...))`.
2. **Comment:** `SkillshotEngine.cs:~44-58` — document that the effective earned bar includes
   the look-ahead (classification-time flown, not crossing-time).
3. **Feel-test flag (todo/README):** the 2-3u crossing band — does an honest mid-close-range
   cross reading "we crossed and got nothing" feel wrong live?
4. **Cosmetic nit:** Solo-degraded merges inherit fused flight (x1.5 speed, midpoint respawn)
   — a "Solo" projectile visibly faster than a normal solo shot. Decide whether that reads as
   a bug live.
