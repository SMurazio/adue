# CRASH1 — server "crash" during live UO play

## ROOT CAUSE (very likely): the build/gate workflow force-stopping the live session — NOT a code bug
The "crash" almost certainly = the orchestrator running gates (`run-checks.cmd` / `godot-build.cmd`) which are
prefixed with `stop-mmo.cmd` to free the `Mmo.Shared.dll`/`Mmo.Server.dll` build lock; `stop-mmo.ps1` does
`Stop-Process -Force` on the server + `godot-client*.pid` PIDs — force-killing the user's running server+client
mid-play. Evidence: the 120c/30s stress gate is clean, the headless UO crash-soak (`UoClientDrivenCrashSoakTests`,
committed `79bdfbf`) does NOT reproduce, and the code-read (`review/review-request-crash1.md`) found every
tick/snapshot/handler path is exception-guarded (swallowed `runtimeFault`, not a process exit). The intermittency
tracks the orchestrator's background builds, not gameplay.

**Action taken:** recorded as a workflow lesson — see memory `dont-gate-while-user-playing.md`. The orchestrator
will warn/ask before any gate that stops the server, and not preemptively `stop-mmo` in the background.

**To fully confirm:** next time the server "crashes," check whether it coincided with the orchestrator running a
build, and/or run with `start-server.cmd -LogToFile` (tees to `.run/server.log` + `.run/server.err.log`) — a
clean log with no unhandled-exception stack at the tail = it was the force-stop, not code.

## Remaining (OPTIONAL, low priority) — H2 defensive hardening, separate commit
Independent of the above, the ONLY genuinely unguarded loop-thread surface is the LiteNetLib event handlers
(`OnConnectionRequest`/`OnPeerConnected`/`OnPeerDisconnected`, fired inside `PollEvents()` outside
`_runtimeGuard`); riskiest line `_zone.Despawn(session.EntityId!.Value, …)` on a disconnect mid-burst. Wrapping
those three handlers in `_runtimeGuard.TryRun` (logged fault + drop that one peer, instead of taking down the
loop) is good robustness — but it is NOT the reported crash. Land it as its own small commit if/when prioritized.

## Status
Code-crash investigation effectively RESOLVED (workflow cause). Keep this file only for the optional H2 guard;
close it when that lands or when the user confirms the force-stop correlation and de-prioritizes the guard.
