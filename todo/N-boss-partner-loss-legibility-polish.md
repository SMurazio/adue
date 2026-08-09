# N — Partner-loss legibility + fight-length polish (from the duo-loss-legibility review)

Two non-blocking notes from the independent review of `feat/boss-duo-loss-legibility` (verdict SHIP).
The batch shipped; these are optional polish.

1. **[NIT — wording]** `BossEncounterEngine.cs` (~1723, the one-shot bond-broken announce, e.g.
   "…the Sunderer yields to a lone strike."). Now that this line fires in P1/P2 too, "a lone STRIKE"
   is literally accurate only for the P3 ward (single hit); in the plating phase the solo path is
   **3 hits in 6s**, so a player may expect one hit to break it and see nothing on hits 1-2. Consider
   phrasing that doesn't imply a single strike suffices in the plating phase (needs a human eye for
   tone — flavor text). Cosmetic.

2. **[FEEL-FLAG — human playtest owed]** A duo-spawned survivor now grinds the full **duo-HP boss
   (1200)** through solo shatter windows — correct and strictly better than the prior unwinnable slog,
   but a long solo fight by construction. If live play shows it drags, the tuning lever is a
   partner-loss HP rebate on the *remaining* pool (NOT spawn HP) — a future knob, not a fix here.
   Flag alongside the other duo feel-tests in the README.
