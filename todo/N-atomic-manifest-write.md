# N — atomic manifest write for Save (Low, from the Part B review)

`GameServer.TrySaveMonsterTypes` uses `File.WriteAllText` (truncate-then-write) on `MonsterManifestPath`. A crash
EXACTLY mid-write could leave the output-dir `monsters.json` corrupt. LOW + already mitigated: `LoadMonsterTypes`
catches any parse failure and falls back to the code seed (the server still starts; the user just loses the unsaved
tweaks), and the repo SOURCE manifest is untouched (a rebuild re-clobbers the output copy).

**Fix (if hardening wanted):** write to a temp file in the same dir, then `File.Move(temp, path, overwrite: true)`
(atomic replace on the same volume). Eliminates the corruption window. Trivial; deferred as Low. Also (Nit) the write
is synchronous on the tick thread — negligible for a few-KB manifest, revisit only if manifests grow large.

From the Save-to-manifest (Part B) review. Builds on [[monster-behavior-architecture]].
