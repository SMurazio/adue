# N — P1 partner loss = unannounced 25%-damage slog with no shatter path (partner-loss review LOW-2)

The P3 downgrade (9ff54ad) deliberately scoped to the ward gate. Its P1 twin remains: a
duo-spawned survivor whose partner is lost in P1 has NO shatter path — fusion is impossible
solo, and the solo hit-count fallback is gated on `_participantsAtSpawn >= 2`
(`BossEncounterEngine.cs` ~880), leaving DuoDamageReduction 0.75 with no vulnerability window
and no announce until P3. Not a dead run (plating dies permanently at 70%) but the same class
of unannounced slog.

Fix: extend the effectively-duo recompute to the P1 gates — when live participants < 2, enable
the solo hit-count shatter fallback (and consider the solo damage-reduction 0.40) + reuse the
one-shot bond-broken announce from StepP3 for P1/P2.

Acceptance: headless test — duo spawn, partner lost in P1, survivor's 3-hits-in-6s opens the
Good window; both-live duo still cannot use the hit-count path.
