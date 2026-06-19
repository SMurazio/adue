# S62 — Deep study of ClassicUO (client) + ModernUO (server) → takeaways doc

Severity: research. Mine two mature, battle-tested UO codebases for the best solutions to adopt **while
keeping our Godot 3D client + our C# server** — NOT a 1:1 port. Priority: **movement** (ours is close but
not there yet). Output: `docs/uo-reference-takeaways.md` — findings mapped to concrete, prioritized
recommendations for OUR stack. Each code change that comes OUT of it gets its own todo + commit.

## Sources
- **ClassicUO** (client, C#/MonoGame): GitHub `ClassicUO/ClassicUO`, source under
  `src/ClassicUO.Client/`. Known-relevant: `Game/Managers/WalkerManager.cs`, `Game/GameObjects/Mobile.cs` +
  `PlayerMobile.cs`, `Game/Data/MovementSpeed.cs`, `Game/Constants.cs`, `Game/Pathfinder.cs`,
  `Game/Scenes/GameSceneInputHandler.cs`, `Game/Data/Direction.cs`. (raw URL:
  `https://raw.githubusercontent.com/ClassicUO/ClassicUO/main/<path>`).
- **ModernUO** (server, C#/.NET): local `D:\UO-Project\server\modernuo`, engine in `Projects/Server/`,
  game logic in `Projects/UOContent/`.

## Method (Orchestrator fans out read-only researchers, then synthesizes)
Four focused researchers — ClassicUO-movement, ModernUO-movement, ClassicUO-architecture,
ModernUO-architecture — each produces structured findings + per-finding "what we should adopt given Godot
/ our server." Orchestrator synthesizes into the takeaways doc, prioritized, movement first.

## Already known (from a first pass)
- Timing: foot walk 400ms / run 200ms; mount 200/100; `TURN_DELAY=80` (fast 45); `WALKING_DELAY=150`;
  `MAX_STEP_COUNT=5` in-flight.
- Mouse: direction = screen-center→cursor; **no dead-zone**; run when cursor ≥190px from center.
- Our turn cost = a full 150ms step (≈2× UO's 80ms) → likely why the turn feels heavy.

## Acceptance
- `docs/uo-reference-takeaways.md` exists: per-subsystem findings (movement, prediction/reconciliation,
  AOI/sectors, rendering/2.5D, entity model, persistence, network) each mapped to a concrete recommendation
  for our Godot client / C# server, prioritized, with the highest-value movement items called out and
  ready to become their own todos.
