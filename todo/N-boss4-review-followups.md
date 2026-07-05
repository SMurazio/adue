# N — BOSS-4 review follow-ups (Fable review of 4deea5e, SHIP-WITH-FOLLOWUPS)

The unkillable-boss class was verified CLEAN (burst window is the only P3 damage path and is always
openable — duo, degraded duo, and solo, with no cooldown trap). Both MEDIUMs and both LOWs were
fixed immediately (Cancel-not-ClearEntity for the mid-air root; CoreRootTile + 16u beam for the
south beam-safe band; exclude the rooted boss from monster separation; the duo+solo onBlast test).
Remaining, acknowledged and not blocking:

- **LOW-3 (accepted deviation) — a sweep beam scheduled up to 1.2s pre-victory still resolves after
  teardown and can hit a departing victor for 25.** Consistent with the decided
  telegraph-outlives-caster rule (a cast telegraph always resolves on its deadline). Only revisit if
  a victor eating a posthumous beam reads badly at the feel-test — the fix would be to cancel
  in-flight beam telegraphs on victory, which crosses the encounter/scheduler boundary.
- **NIT — enrage re-paces the beam to 47 ticks (not a multiple of 10), so the beam/knockback residue
  disjointness drifts under enrage.** Genuinely harmless (the pulse shove deals no damage, so a
  same-tick beam+shove is fine); noted at BossEncounterEngine.cs. Only tidy if the residue invariant
  is ever leaned on for something damaging.

## Live feel-test watch items (the human gate for the whole arc)
- Boss renders steel-grey (sealed) from the first frame at 40%, drops to normal on a burst window.
- The rotating beam reads as a consistent-direction sweep you can walk with; no permanent safe spot.
- Knockback-vs-detonation-aim tension in P3 feels like the intended pull, not random shove.
- The ward-breaking detonation dealing 0 direct damage (it's a KEY): if "my big blast hit for
  nothing" reads as a lie, flip the blast report to before the damage loop (one line) so it lands
  inside its own window.
- The rooted boss's dormant melee (no cleave up close): confirm it doesn't feel passive.
