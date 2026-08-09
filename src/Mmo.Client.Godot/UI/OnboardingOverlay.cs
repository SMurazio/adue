using Godot;
using Mmo.Client.Core;

namespace Mmo.Client.Godot.UI;

// ADUE P2-B (todo/S-p2-onboarding-verb-hints.md): the screen-space RENDER of the onboarding hints. It owns no
// decisions — MmoClientRoot's OnboardingCoach computes an OnboardingHintView each frame and hands it to Apply(); this
// control just draws it. Two calm, non-flashing surfaces (Law-7 teaching tone, matching the boss teach-label voice):
//   * a PAIRING banner, top-centre, shown while unpaired;
//   * a VERB panel, lower-left, listing Q/R/G/V with what each does (the Q line teaches the CROSS→fuse), shown while
//     inside the practice room; a verb dims once the local player has used it.
// Built programmatically once in _Ready (four fixed verb rows in Q/R/G/V order); Apply only toggles visibility and
// writes text/dim, so there is no per-frame node churn. MouseFilter is Ignore throughout so it never eats input.
//
// The layout, sizes, colours and placement here are a FIRST PASS — a human feel-test owns whether it reads well and
// actually teaches; this file only guarantees the right strings appear in the right show/hide states.
public partial class OnboardingOverlay : Control
{
    // Calm pale cyan-white — the same "persistent teaching cue, not an alert" colour the boss plating label uses
    // (EntityVisual.ProtectionLabelColor). Deliberately NOT red and NOT animated.
    private static readonly Color TeachColor = new(0.80f, 0.92f, 0.96f);

    // A slightly warmer highlight for the pairing call-to-action so it reads as the primary ask, still calm.
    private static readonly Color PairingColor = new(0.98f, 0.90f, 0.62f);

    // Used verbs dim to this modulate (kept legible, just clearly "done").
    private static readonly Color UsedRowModulate = new(1f, 1f, 1f, 0.38f);
    private static readonly Color FreshRowModulate = Colors.White;

    private const int PairingFontSize = 22;
    private const int VerbHeadingFontSize = 18;
    private const int VerbKeyFontSize = 18;
    private const int VerbTeachFontSize = 14;

    private PanelContainer? _pairingPanel;
    private Label? _pairingLabel;

    private PanelContainer? _verbPanel;
    private Label? _verbHeading;

    // Four fixed rows, index 0..3 == Q/R/G/V (the order OnboardingCoach.Select emits). Each row is a VBox holding a
    // "Q — Fusion Skillshot" title line + a wrapped teach line; the whole row's modulate dims when used.
    private readonly Control[] _verbRows = new Control[4];
    private readonly Label[] _verbTitles = new Label[4];
    private readonly Label[] _verbTeach = new Label[4];

    // Cheap change-guard so Apply only rewrites text when the view actually changed (visibility + used-bitmask).
    private int _lastSignature = -1;

    public override void _Ready()
    {
        // Fill the screen; never intercept input.
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildPairingBanner();
        BuildVerbPanel();

        // Start hidden until the first Apply.
        if (_pairingPanel is not null) _pairingPanel.Visible = false;
        if (_verbPanel is not null) _verbPanel.Visible = false;
    }

    // Top-centre banner: a translucent dark plate behind a centred, wrapped label.
    private void BuildPairingBanner()
    {
        _pairingPanel = new PanelContainer
        {
            Name = "PairingBanner",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.End,
            OffsetTop = 24f,
            OffsetLeft = -320f,
            OffsetRight = 320f,
        };
        _pairingPanel.AddThemeStyleboxOverride("panel", MakePlate());

        _pairingLabel = new Label
        {
            Name = "PairingLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _pairingLabel.AddThemeColorOverride("font_color", PairingColor);
        _pairingLabel.AddThemeFontSizeOverride("font_size", PairingFontSize);
        _pairingPanel.AddChild(_pairingLabel);
        AddChild(_pairingPanel);
    }

    // Lower-left panel: heading + four verb rows.
    private void BuildVerbPanel()
    {
        _verbPanel = new PanelContainer
        {
            Name = "VerbPanel",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0f,
            AnchorRight = 0f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.End,
            GrowVertical = GrowDirection.Both,
            OffsetLeft = 24f,
            OffsetRight = 24f + 360f,
        };
        _verbPanel.AddThemeStyleboxOverride("panel", MakePlate());

        var col = new VBoxContainer { Name = "VerbColumn", MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 10);

        _verbHeading = new Label { Name = "VerbHeading", MouseFilter = MouseFilterEnum.Ignore };
        _verbHeading.AddThemeColorOverride("font_color", TeachColor);
        _verbHeading.AddThemeFontSizeOverride("font_size", VerbHeadingFontSize);
        col.AddChild(_verbHeading);

        for (var i = 0; i < _verbRows.Length; i++)
        {
            var row = new VBoxContainer { Name = $"VerbRow{i}", MouseFilter = MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 2);

            var title = new Label { Name = "Title", MouseFilter = MouseFilterEnum.Ignore };
            title.AddThemeColorOverride("font_color", TeachColor);
            title.AddThemeFontSizeOverride("font_size", VerbKeyFontSize);
            row.AddChild(title);

            var teach = new Label
            {
                Name = "Teach",
                MouseFilter = MouseFilterEnum.Ignore,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            teach.AddThemeColorOverride("font_color", TeachColor);
            teach.AddThemeFontSizeOverride("font_size", VerbTeachFontSize);
            row.AddChild(teach);

            _verbRows[i] = row;
            _verbTitles[i] = title;
            _verbTeach[i] = teach;
            col.AddChild(row);
        }

        _verbPanel.AddChild(col);
        AddChild(_verbPanel);
    }

    // A translucent dark rounded plate so light teach text stays legible over any terrain.
    private static StyleBoxFlat MakePlate()
    {
        var plate = new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.08f, 0.72f) };
        plate.SetCornerRadiusAll(8);
        plate.SetContentMarginAll(14);
        plate.BorderColor = new Color(0.30f, 0.42f, 0.48f, 0.85f);
        plate.SetBorderWidthAll(1);
        return plate;
    }

    // Render one frame's view. Cheap: a signature guard skips the text rewrite when nothing changed; visibility is
    // still applied every call (near-free) so a hide always lands.
    public void Apply(OnboardingHintView view)
    {
        if (_pairingPanel is not null) _pairingPanel.Visible = view.ShowPairingPrompt;
        if (_verbPanel is not null) _verbPanel.Visible = view.ShowVerbHints;

        var signature = Signature(view);
        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;

        if (view.ShowPairingPrompt && _pairingLabel is not null)
        {
            _pairingLabel.Text = view.PairingPrompt;
        }

        if (view.ShowVerbHints)
        {
            if (_verbHeading is not null)
            {
                _verbHeading.Text = view.VerbHeading;
            }

            for (var i = 0; i < _verbRows.Length; i++)
            {
                if (i < view.VerbHints.Count)
                {
                    var hint = view.VerbHints[i];
                    _verbTitles[i].Text = $"{hint.Key} — {hint.Name}";
                    _verbTeach[i].Text = hint.Teach;
                    _verbRows[i].Visible = true;
                    _verbRows[i].Modulate = hint.Used ? UsedRowModulate : FreshRowModulate;
                }
                else
                {
                    _verbRows[i].Visible = false;
                }
            }
        }
    }

    // Compact change key: which panels show + which verbs are marked used. Text itself is constant per (verb,used)
    // so this fully captures a render-affecting change.
    private static int Signature(OnboardingHintView view)
    {
        var sig = 0;
        if (view.ShowPairingPrompt) sig |= 1;
        if (view.ShowVerbHints) sig |= 2;
        for (var i = 0; i < view.VerbHints.Count; i++)
        {
            if (view.VerbHints[i].Used)
            {
                sig |= 1 << (2 + i);
            }
        }

        return sig;
    }
}
