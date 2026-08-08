# N — Opposite-side boss hug nullifies the knockback contest (ward-break review MEDIUM — feel-test first)

With the c2c03dd gate, the dominant P3 strategy becomes: pair stands diametrically on the
rooted boss at ~2u each — separation 4u (passes), midpoint = boss center exactly (passes), and
the RADIAL knockback pulse shoves both players symmetrically outward, leaving the midpoint
invariant and separation growing. The pulse never disturbs this aim, so the design doc's
"aim the midpoint through the shoves" is untrue against it.

This at least requires two-brain symmetric positioning (arguably acceptable play, unlike
stacking). FEEL-TEST FIRST — do not fix preemptively. If it feels degenerate live, the fix is
to gate on midpoint-to-nearest-player distance (forces the midpoint AWAY from both bodies)
instead of / in addition to pair separation.

Related LOW from the same review (fold in if fixing): a dead confirmer mid-charge still
resolves against the corpse position (death does not BreakPair), so a corpse parked 4u out is
a valid separation anchor. And a missing test: stacked-at-confirm/separated-by-resolve blast
PASSES (pins that separation is read at resolve, not lock-in).
