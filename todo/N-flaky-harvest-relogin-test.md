# N — Flaky test: InteractHarvestIntegrationTests.HarvestSurvivesSameAccountTakeoverRelogin

Failed once then passed on immediate re-run during an UNRELATED client-cosmetic gate (2026-07-05),
with no code change between runs. Real-socket + SQLite + relogin integration test (~6s) — classic
timing flake. Not investigated (causally impossible to be affected by the client-render change that
surfaced it). If it recurs, look at the takeover/relogin handshake timing or a fixed wait that races
under load; consider a WaitUntil instead of a fixed delay.
