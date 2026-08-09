using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// ADUE P2-C (todo/N-p2-title-and-floor-frame.md, docs/duo-living-tower.md): the PURE, headless-testable copy brain for
// the roguelite RUN FRAMING — the light "which floor / how far did we get" dressing over the existing single-room run
// (UpdateRunPanel in the Godot layer). There is exactly ONE floor today, so this is framing, not content: it answers
// "given a run outcome + the boss's leftover HP%, what's the end-screen headline and the one-line 'how far you got'
// beat?" and hands back plain strings the Godot layer renders verbatim. No Godot types here on purpose, so every
// framing string and every outcome→copy decision is unit-tested in Mmo.Client.Core.Tests; the label layout/legibility
// and the copy's FEEL stay a HUMAN feel-test (this class only decides WHAT the words are, never how they look).
//
// Deliberately reads (not writes) the existing replicated RunSummary fields (RunOutcome + BossHealthPercent): NO new
// wire field, NO protocol change — a single-room run means "how high we climbed" is cosmetic.

// The two end-screen framing lines for one finished run. Headline is the outcome verdict (the existing "RUN CLEARED" /
// "RUN OVER — WIPED" beat); FloorBeat is the new one-line "how far you got" line rendered ABOVE the cheap stats.
public readonly record struct RunEndScreenLines(string Headline, string FloorBeat);

public static class RunFraming
{
    // The one floor the run ships today (docs/duo-living-tower.md — the multi-floor descent is a later seam). Named so
    // the "FLOOR 1" literal lives in exactly one place and the eventual real floor number threads through here.
    public const int CurrentFloor = 1;

    public const string BossName = "The Sunderer";

    // The ACTIVE-run banner framing, e.g. "FLOOR 1 · The Sunderer" — the encounter named as a numbered floor.
    public static readonly string FloorLabel = $"FLOOR {CurrentFloor} · {BossName}";

    // A wipe where the boss was left at or under this HP% (but not already dead) reads as a "so close" near-miss beat
    // instead of the flat "still stands" line — the one place BossHealthPercent shapes the copy. FEEL-test owed: the
    // threshold and both wipe strings are a first cut.
    public const byte CloseCallPercent = 20;

    public const string ClearHeadline = "RUN CLEARED";
    public const string WipeHeadline = "RUN OVER — WIPED";

    // Abandoned/None never actually reach a client end screen (there is nobody left to show it to — see RunOutcome),
    // but Compose is total, so it returns a graceful, deterministic verdict rather than an empty string.
    public const string EndedHeadline = "RUN ENDED";

    public static readonly string ClearBeat = $"FLOOR {CurrentFloor} CLEARED";
    public const string StandsBeat = "The Sunderer still stands.";
    public const string CloseCallBeat = "The Sunderer reeled — but still stands.";

    // The pure outcome→copy decision. Given how the run ended and the boss's leftover HP%, returns the end-screen
    // headline + the one-line floor beat. Total over every RunOutcome (clear / wipe / abandoned / none).
    public static RunEndScreenLines Compose(RunOutcome outcome, byte bossHealthPercent) => outcome switch
    {
        RunOutcome.Clear => new RunEndScreenLines(ClearHeadline, ClearBeat),
        RunOutcome.Wipe => new RunEndScreenLines(
            WipeHeadline,
            bossHealthPercent is > 0 and <= CloseCallPercent ? CloseCallBeat : StandsBeat),
        // Abandoned + None (and any future value): a run that ended with the boss alive but no clear/wipe verdict.
        _ => new RunEndScreenLines(EndedHeadline, StandsBeat),
    };
}
