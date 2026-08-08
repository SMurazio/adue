# Plan — standalone duo co-op game in a separate repo

Status: DRAFT (2026-08-08) — awaiting the user's go + naming decisions.
Context: two independent Fable design reviews (2026-08-08) converged: the duo grammar's strongest
reading is a **session-shaped 2-player action roguelite** (contested-channel bosses, ~30-45 min
runs, couch + online co-op), not a persistent MMO. The user wants to explore that in a separate
repo **without discarding the multiplayer work in this one**.

## Principles

1. **Nothing is thrown away.** The duo repo is a **full-history fork** of this repo — every line
   of netcode, every commit, every design doc travels with it. This repo (`D:\MMO`) remains the
   MMO / Living Tower codebase, untouched and independently continuable.
2. **Prune on friction, not on principle.** MMO-only systems (persistence, ecology, AOI-at-scale,
   spawner/world sim) stay in the duo repo's tree until they actually block a change. Deleting is
   always possible later; un-deleting across diverged history is painful.
3. **Accept divergence.** No submodules / shared-package machinery between the two repos — solo-dev
   overhead isn't worth it. A fix that matters to both gets cherry-picked across by hand.
4. **The two repos keep separate identities**: separate memory/todo/review queues from day one, so
   the loop's discipline (one todo = one commit, review independence) applies per-repo.

## Fork mechanics (Phase 0)

- Land the three duo-grill surgical fixes **here first** (`S-duo-grill-ward-break-separation`,
  `S-duo-grill-fusion-pointblank`, `S-duo-grill-echo-cue-render`) so both repos inherit them from
  the shared history instead of double-fixing after divergence.
- Fork point: the tip of `exp/duo-abilities` (duo kit v50 + Sunderer + telegraph shapes) — the most
  duo-complete state of the tree.
- Mechanics: `git clone D:\MMO <new-location>` → new default branch (e.g. `main` reset to the fork
  point) → new remote (fresh private GitHub repo). Full history preserved by construction.
- Duo repo bootstrap: its own `CLAUDE.md`/`.shared/project.md` (same loop contract, duo-game
  identity), fresh `todo/` seeded from the phases below, fresh memory index.

**User decisions (RESOLVED 2026-08-08):** name **Adue** (title "Adue", logo mark "a2" — the
score notation for two divided instruments rejoining on a single line), slug/location
`D:\Adue`, private GitHub repo `SMurazio/adue`, fork AFTER the grill fixes. The fix-then-fork
set grew to five commits during review: the three grill fixes (c2c03dd ward-break, 6937739
echo-cue, d1fb411 fusion) + two review-driven: 9ff54ad partner-loss downgrade, 5131d78
Solo-tier 3s shatter window. Fork point = branch tip once the final review verdict is clean.

## What carries vs what parks (per the design review, ~60% survives)

**Load-bearing in the duo game:** continuous movement + prediction, honest render=hit telegraph
protocol (WEDGE/LINE/CIRCLE), movement actions (jump/charge/dodge + i-frames), entity collision,
the four duo engines (Skillshot/Shield/Tether/Midpoint), BossEncounterEngine + the Sunderer,
monster behavior framework, damage choke point, the headless test harnesses, Godot client.

**Parked (keep in tree, prune on friction):** SQLite persistence, ecology E0-E4, AOI/interest
management at scale, spawner/world-sim, stress fleet tooling (partially keep — still the perf gate),
chat-command surface (replaced by real UI over time).

## Architecture deltas the duo game needs (the review's bounded rearchitecture)

- **Bundled host-side server**: the single-process .NET server ships inside the game and is
  launched by the host client as a local child process (loopback connect). No dedicated-server ops
  for a $20 game. This is a launcher/packaging task, not a server rewrite.
- **Couch co-op mode**: two players on ONE client (second input device, shared camera). Biggest
  new client work in the plan — but it dissolves the Law-11 sync-window latency debt entirely
  (one clock), making the timing-class mechanics fully available. Online co-op keeps the existing
  client-server path (+ Steam Remote Play Together for free online-couch).
- **Lobby/pairing UX** replaces `/pair` chat commands (consent built in by construction).

## Phases (duo repo's initial todo seed)

- **P0 — Fork + boots**: repo created per above; server + client build green; a duo can run around
  and fight the Sunderer in the new repo. (Days.)
- **P1 — Run loop skeleton**: start-run → arena/floor → boss → death-or-clear → end screen →
  restart. Strips "persistent world" out of the moment-to-moment loop. This is the roguelite chassis.
- **P2 — Demo slice for the stranger-duo test** (the kill test all three reviews converged on):
  P1 chassis + the duo kit + the Sunderer + minimal onboarding, packaged as a downloadable
  two-player build. Pass criteria (from the framework itself): do stranger duos attempt the Perfect
  version unprompted, and do they laugh? **This gates all further investment.**
- **P3 — only if P2 passes**: couch mode, bundled host server, first couple-items via the
  component-modifier loot scheme, second boss derived from the inversion generator.
- **Art/VFX/music remain the acknowledged unsolved workstream** (not AI-agent territory); the demo
  ships with committed-low-fi placeholder style, and the market-pull probe (Steam page + clip)
  waits until there is real art direction.

## What happens to this repo

`D:\MMO` continues as the MMO codebase. The Living Tower direction stays documented and viable;
its open questions (layer-vs-pivot, one-world transformation) stay parked in the design docs until
the P2 stranger-duo result produces evidence. No MMO systems are deleted anywhere.
