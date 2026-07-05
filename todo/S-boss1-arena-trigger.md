# S — BOSS-1: Sunderer arena + /boss trigger + encounter lifecycle

Per docs/boss-encounter-sunderer-design.md ("Implementation plan" — BOSS-1). Arena stamp (24x24,
far corner, node-masked) + hash re-pins from first gate; /boss command (teleport both pair members,
return position, 3s countdown, leave/reset/victory rules); boss manifest entry + encounter engine
scaffold (TelegraphScheduler-style injected seam) with Cleave + Lunge only. HIGHEST RISK: teleport
must land as a hard predictor snap, not a cross-map lerp.
