# N — Outsider pair can break the P3 ward through the sealed arena wall (partner-loss review LOW-1, pre-existing since BOSS-4)

`BossEncounterEngine.OnMidpointBlast` accepts ANY blast report filtered only by center
distance — never by authorship/participation. A second paired duo standing outside opposite
exterior walls (e.g. (368,355)/(368,382); midpoint ~= CoreRootTile (368,368)) produces a
Good/Perfect blast with ~27u separation that passes the duo gate, breaks the ward, and
damages the boss through the wall (`MidpointDetonationEngine.ResolveBlast` has no LOS,
~221-229). Needs 4 colluding players; low priority, but it violates the sealed-pocket premise.

Fix options: require the blast initiator to be an encounter participant (cleanest — thread
initiator id through the blast report), or LOS/arena-membership check on the blast center.

Acceptance: headless test — non-participant pair's blast at the ward center does not break the
ward; participant pair unchanged.
