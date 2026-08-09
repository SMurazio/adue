using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// ADUE P2 (todo/S-p2-landing-screen.md): the pure landing-screen copy brain. These tests pin the visibility gate
// (Lobby-only) and the live ready-state → prompt-line decision the Godot renderer trusts. The full-screen layout, the
// title/mark sizes, and the a2 placeholder's FEEL are a HUMAN feel-test, not covered here.
public sealed class LandingScreenTests
{
    // ---- visibility: Lobby only --------------------------------------------------------------------------

    [Fact]
    public void Compose_Lobby_IsVisible()
    {
        var view = LandingScreen.Compose(RunPhase.Lobby, isPaired: true, selfReady: false, readyCount: 0, rosterCount: 2, partnerName: "Ada");
        Assert.True(view.Visible);
    }

    [Theory]
    [InlineData(RunPhase.Active)]
    [InlineData(RunPhase.Summary)]
    public void Compose_ActiveOrSummary_IsHiddenWithNoPrompt(RunPhase phase)
    {
        var view = LandingScreen.Compose(phase, isPaired: true, selfReady: true, readyCount: 2, rosterCount: 2, partnerName: "Ada");

        Assert.False(view.Visible);
        Assert.Equal(string.Empty, view.Prompt);
    }

    [Fact]
    public void ShouldShow_TracksLobbyOnly()
    {
        Assert.True(LandingScreen.ShouldShow(RunPhase.Lobby));
        Assert.False(LandingScreen.ShouldShow(RunPhase.Active));
        Assert.False(LandingScreen.ShouldShow(RunPhase.Summary));
    }

    // ---- the four ready states the prompt must reflect ---------------------------------------------------

    [Fact]
    public void Prompt_Unpaired_Connecting_ShowsWaitingForPartner()
    {
        // The pre-login connecting gap / partner not online yet: solo roster, no pair.
        var prompt = LandingScreen.PromptLine(isPaired: false, selfReady: false, readyCount: 0, rosterCount: 1, partnerName: null);

        Assert.Equal(LandingScreen.ConnectingPrompt, prompt);
        // Reuses the OnboardingCoach waiting copy — one source of truth for the "waiting for your partner" voice.
        Assert.Equal(OnboardingCoach.WaitingForPartnerLine, prompt);
        Assert.DoesNotContain("/pair", prompt);
    }

    [Fact]
    public void Prompt_SoloRoster_TreatedAsConnecting_EvenIfPairFlagLingers()
    {
        // Defensive: a lone roster is still "waiting", regardless of a stale isPaired.
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: false, readyCount: 0, rosterCount: 1, partnerName: "Ada");
        Assert.Equal(LandingScreen.ConnectingPrompt, prompt);
    }

    [Fact]
    public void Prompt_PairedNeitherReady_InvitesBothToBegin()
    {
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: false, readyCount: 0, rosterCount: 2, partnerName: "Ada");

        Assert.Equal(LandingScreen.NeitherReadyPrompt, prompt);
        Assert.Contains("[B]", prompt);
        Assert.Contains("together", prompt);
    }

    [Fact]
    public void Prompt_SelfReadyOnly_WaitsOnNamedPartner()
    {
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: true, readyCount: 1, rosterCount: 2, partnerName: "Ada");

        Assert.Contains("You're ready", prompt);
        Assert.Contains("Ada", prompt);
    }

    [Fact]
    public void Prompt_SelfReadyOnly_NullPartner_UsesFallbackName()
    {
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: true, readyCount: 1, rosterCount: 2, partnerName: null);

        Assert.Contains("You're ready", prompt);
        Assert.Contains(LandingScreen.PartnerFallbackName, prompt);
    }

    [Fact]
    public void Prompt_PartnerReadyOnly_NudgesSelfToCommit()
    {
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: false, readyCount: 1, rosterCount: 2, partnerName: "Ada");

        Assert.Contains("Ada", prompt);
        Assert.Contains("[B]", prompt);
        Assert.Contains("ready", prompt);
    }

    [Fact]
    public void Prompt_PartnerReadyOnly_NullPartner_CapitalizesFallback()
    {
        // Sentence-start capitalization of the fallback name ("Your partner is ready…").
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: false, readyCount: 1, rosterCount: 2, partnerName: null);

        Assert.StartsWith("Your partner is ready", prompt);
    }

    [Fact]
    public void Prompt_BothReady_ShowsDescending()
    {
        var prompt = LandingScreen.PromptLine(isPaired: true, selfReady: true, readyCount: 2, rosterCount: 2, partnerName: "Ada");
        Assert.Equal(LandingScreen.BothReadyPrompt, prompt);
    }

    // ---- branded copy invariants -------------------------------------------------------------------------

    [Fact]
    public void BrandedCopy_IsPresentAndNonEmpty()
    {
        Assert.Equal("ADUE", LandingScreen.Title);
        Assert.Equal("a2", LandingScreen.Mark);
        Assert.False(string.IsNullOrWhiteSpace(LandingScreen.Tagline));
        Assert.False(string.IsNullOrWhiteSpace(LandingScreen.Nudge));
    }
}
