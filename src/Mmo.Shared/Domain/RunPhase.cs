namespace Mmo.Shared.Domain;

// ADUE P1 (todo/S-adue-p1-run-loop-chassis.md, docs/duo-standalone-plan.md P1): the roguelite RUN chassis' shared
// vocabulary. Both the server's RunEngine and every client mirror these bytes over the wire (RunStatusMessage /
// RunSummaryMessage), so the enum lives in Mmo.Shared.Domain next to the other cross-cut wire enums
// (EntityKind/TetherState/EchoCueKind) rather than being duplicated per side.
//
// The run shape P1 ships: LOBBY (ready up in town) -> ACTIVE (the run is live; in P1 its ONLY room is the Sunderer
// boss arena, reusing the existing BossEncounterEngine) -> SUMMARY (the end screen: clear/wipe + cheap stats) ->
// back to LOBBY, all without a server restart.
public enum RunPhase : byte
{
    // No run in progress. Players stand in town and ready up (RunReady). A paired duo starts when BOTH are ready;
    // an unpaired player starts solo on their own ready.
    Lobby = 0,

    // A run is live. P1 has exactly one room (the boss arena), so "Active" == "in the boss room"; the multi-floor /
    // wave descent is the P2 seam (see RunEngine's header note) and would become a sub-stage of this phase.
    Active = 1,

    // The run has ended and the end screen is up. The roster has been returned to town (revived + teleported); the
    // phase auto-drops back to Lobby after the summary window, or immediately when someone readies up again.
    Summary = 2,
}

// How a run ENDED — the end screen's headline. Abandoned never reaches a client (there is nobody left to show it
// to); it exists so the reset path is explicit rather than an unlabeled fallthrough.
public enum RunOutcome : byte
{
    // No run has ended yet (the Lobby-phase value).
    None = 0,

    // The Sunderer died: the pair cleared the run.
    Clear = 1,

    // Every roster member was down at once (solo death counts): the run wiped.
    Wipe = 2,

    // Everybody left/disconnected before an outcome existed. No end screen — straight back to Lobby.
    Abandoned = 3,
}
