# N — Gated ward blasts are silent (ward-break review MEDIUM)

`BossEncounterEngine.cs:~1264-1267`: a blast rejected by the new duo-mode gate (tier too low
or pair separation < 4u) returns with no announce, cue, or deflect feedback. A pair landing a
perfectly centered Good blast at 3.9u separation sees NOTHING — indistinguishable from a bug,
and it contradicts b188aa8's protected-state legibility work (deflected hits + teach labels).

Fix: reuse the existing deflect/teach-label path for the two new refusal modes, with distinct
one-line teach text ("The ward only yields to a true duo strike" / "Strike from farther
apart"). Server-side announce or cue reuse only — no protocol change expected.

Acceptance: headless test that a gated blast produces the deflect/teach feedback; human
feel-test flagged in todo/README.
