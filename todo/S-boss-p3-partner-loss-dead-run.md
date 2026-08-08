# S — P3 partner loss now leaves the survivor with NO ward-break path (ward-break review HIGH)

## Problem

Introduced by c2c03dd (duo-tier + >=4u separation gate). `_participantsAtSpawn` is fixed at
spawn (`BossEncounterEngine.cs:635`) and never downgraded; disconnect/kick call `BreakPair`, so
the survivor's `HandleDetonate` resolves no partner → only the solo self-blast
(`PairTier.None`) is reachable → the duo-mode gate at `BossEncounterEngine.cs:~1264` rejects
every blast the survivor can ever produce. Partner DEATH is equivalent (respawned-to-town
partner's midpoint can never reach the boss; no mid-encounter re-entry). Pre-fix this was
winnable (degenerately). Not a hard softlock (/boss leave + wipe-reset work) but it is an
unannounced dead run against a 1200 HP boss.

## Fix

Downgrade the encounter to solo ward rules when the live participant count drops to 1
(pair broken, partner disconnected, or partner dead past a grace window) — OR, minimally,
announce clearly that the run is lost. Downgrade preferred: it matches the
degradation-everywhere discipline. Also decide the partner-death case: downgrade after death,
or restore duo rules if the partner re-enters range? Keep it simple; surface forks.

## Acceptance

- Headless test: duo P3, partner disconnects → survivor's solo self-blast breaks the ward
  (or, if announce-only chosen, the announce fires).
- Headless test: partner death → same downgrade behavior.
- Existing duo-gate exploit tests still green (stacked pair / lone-V-lapse still rejected
  while BOTH participants are live).
