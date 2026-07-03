# N — Telegraph arc T3: first shipped telegraphed ability content + tuning pass

Depends on T1+T2. The payoff phase: make the dodge dance real on live monsters, then tune by feel.

## Scope

- Slime SLAM shipped as its real attack (replacing/augmenting plain melee per feel): windup/radius/damage/
  cooldown tuned live via the existing data-driven F1 Monster tab knobs + Save.
- A SECOND pattern for the gnoll (e.g., leap-slam telegraph at the charge's landing point, or a cone swipe if
  the cone shape gets built here) — proves the shape seam is generic.
- Behavior integration polish: use-when rules (range bands, don't spam while fleeing), windup ROOT (the caster
  stands still during windup — telegraphs must be honest; use the existing movement-freeze seam).
- HUMAN: the core feel-test — fight a slime + gnoll with dodge-roll only; the K (windup) sizing vs the
  dodge-roll distance (2.5u) is THE tuning knob pair (design doc: K must exceed worst-case latency AND give a
  fair dodge window).

Light rigor: framework proven by T1/T2; implementer + gates; review only if damage/netcode paths change.
