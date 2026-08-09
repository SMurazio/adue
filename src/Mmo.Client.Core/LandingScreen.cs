using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// ADUE P2 (todo/S-p2-landing-screen.md, docs/duo-living-tower.md): the PURE, headless-testable copy brain for the
// branded LANDING screen — the full-screen title that owns the pre-run LOBBY (and the pre-login connecting gap) and
// leads into the run on "press B together to begin". It answers the one question "given the live run/pair state, is the
// landing up, and what is its one prompt line right now?" and hands back plain strings the Godot layer renders verbatim.
// No Godot types here on purpose: every branded string and every ready-state→prompt decision is unit-tested in
// Mmo.Client.Core.Tests; the label layout/sizes and the a2 placeholder mark's FEEL stay a HUMAN feel-test (this class
// decides WHAT the words are and WHETHER it shows, never how it looks).
//
// Reads (never writes) the existing replicated RunStatus fields (phase, ready count, roster count, self-ready) + the
// pair state: NO new wire field, NO protocol change. The landing is a re-presentation of the lobby the run panel used
// to draw as a small face; it does not gate the auto-connect / auto-pair flow — those run in the Godot _Ready.

// The whole landing view for one frame: is it up, and the single live prompt line under the title. The renderer reads
// Visible to show/hide the full-screen overlay and Prompt as the ready-state line; Title/Mark/Tagline/Nudge are the
// static branded strings below.
public readonly record struct LandingView(bool Visible, string Prompt);

public static class LandingScreen
{
    // ---- branded copy (single source; the Godot layer renders these verbatim) ----------------------------

    // The title mark. The a2 monogram is a TEXT placeholder (art pending), matching the duo-card convention.
    public const string Title = "ADUE";
    public const string Mark = "a2";

    // From the score marking *a due* — two divided instruments rejoining on a single line (see .shared/project.md).
    public const string Tagline = "two players, one line";

    // The optional nudge toward the sealed practice pocket; the renderer may draw it small under the prompt.
    public const string Nudge = "…or /practice to warm up first";

    // ---- the live prompt line, per ready state -----------------------------------------------------------

    // Not yet paired (the pre-login connecting gap or the brief window before the partner is online). Mirrors the
    // OnboardingCoach "waiting for your partner" voice — no fake command to type; the server auto-pairs on join.
    public static readonly string ConnectingPrompt = OnboardingCoach.WaitingForPartnerLine;

    // Paired, neither player readied yet — the real "commit together" invitation.
    public const string NeitherReadyPrompt = "Press [B] together to begin.";

    // Paired, only YOU are ready — waiting on the partner (name filled in when known).
    // Composed with the partner name via SelfReadyPrompt.
    public const string PartnerFallbackName = "your partner";

    // Paired, only your PARTNER is ready — the nudge to commit back.
    // Composed with the partner name via PartnerReadyPrompt.

    // Both paired players ready — the run is starting (RunEngine begins the descent).
    public const string BothReadyPrompt = "Both ready — descending…";

    public static string SelfReadyPrompt(string? partnerName) =>
        $"You're ready — waiting for {NameOr(partnerName)}…";

    public static string PartnerReadyPrompt(string? partnerName) =>
        $"{Capitalize(NameOr(partnerName))} is ready — press [B] to begin together.";

    // ---- the pure decision the unit tests drive ----------------------------------------------------------

    // The landing is up ONLY in the LOBBY phase (the pre-run and between-runs state, and — because CurrentRunPhase
    // defaults to Lobby before the first RunStatus lands — the pre-login connecting gap too). HIDDEN during the live
    // run (Active) and the end screen (Summary), which the run panel keeps owning.
    public static bool ShouldShow(RunPhase phase) => phase == RunPhase.Lobby;

    // The full frame decision. Given the phase + the replicated pair/ready state, returns whether the landing shows and
    // its one live prompt line. rosterCount is the run roster size (1 = solo/unpaired, 2 = the duo); readyCount is how
    // many of the roster have readied. partnerName may be null (not yet resolved) — a neutral fallback fills in.
    public static LandingView Compose(
        RunPhase phase, bool isPaired, bool selfReady, int readyCount, int rosterCount, string? partnerName)
    {
        if (!ShouldShow(phase))
        {
            return new LandingView(Visible: false, Prompt: string.Empty);
        }

        return new LandingView(Visible: true, Prompt: PromptLine(isPaired, selfReady, readyCount, rosterCount, partnerName));
    }

    // The pure ready-state → prompt-line decision (no phase gate; ShouldShow/Compose handle visibility). Ordering:
    //   * not paired (solo roster) → the connecting / waiting-for-partner line;
    //   * both ready → the descending line (the run is committing);
    //   * only self ready → waiting on the partner;
    //   * only partner ready → the commit-back nudge;
    //   * neither ready → the plain "press [B] together" invitation.
    public static string PromptLine(bool isPaired, bool selfReady, int readyCount, int rosterCount, string? partnerName)
    {
        // Treat a solo roster as "not paired" even if a stale pair flag lingers — a run needs two, so a lone player is
        // still in the connecting/waiting state.
        if (!isPaired || rosterCount < 2)
        {
            return ConnectingPrompt;
        }

        var partnerReady = readyCount - (selfReady ? 1 : 0) >= 1;

        if (selfReady && partnerReady)
        {
            return BothReadyPrompt;
        }

        if (selfReady)
        {
            return SelfReadyPrompt(partnerName);
        }

        if (partnerReady)
        {
            return PartnerReadyPrompt(partnerName);
        }

        return NeitherReadyPrompt;
    }

    private static string NameOr(string? name) =>
        string.IsNullOrWhiteSpace(name) ? PartnerFallbackName : name!;

    // Capitalize the first letter for use at the START of a sentence ("Ada is ready" / "Your partner is ready").
    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
