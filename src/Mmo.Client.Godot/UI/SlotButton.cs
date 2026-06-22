using Godot;

namespace Mmo.Client.Godot.UI;

// S108 (HUD slice 3): the reusable action-bar slot. One SlotButton renders a single consumable / autoattack /
// spell cell of the bottom-center bar: an icon, a normal-vs-amber "selected" frame, a keybind label beneath, an
// optional stack-count badge (bottom-right), and a cooldown overlay (darken + centered whole-second countdown).
//
// The scene (SlotButton.tscn) wires this script onto the root and lays out the named child controls this code
// looks up. The HUD (Hud.cs) instances the scene for every slot, calls Configure() once to set the static look
// (icon texture, keybind glyph), then each frame calls Apply() with the live count / selected / cooldown values
// pulled from HudState. Cooldown COUNTDOWN itself runs locally here in _Process (client-only, no server) so it
// keeps ticking smoothly between the HUD's throttled HudState pushes — matching docs/hud-ui-design.md.
//
// Presentation-only: reads nothing from movement/snapshot/prediction; the only state it owns is the local
// cooldown remaining-seconds timer it ticks down to zero.
public partial class SlotButton : Control
{
    private TextureRect? _icon;
    private Panel? _frame;
    private Label? _keybind;
    private Label? _count;
    private ColorRect? _cooldownShade;
    private Label? _cooldownText;

    // COMBAT-TUNING (radial cooldown): an overlay drawn as a pie-slice sweep (a "radial wipe") for slots driven by a
    // REAL remaining fraction (the LMB autoattack). Added lazily on first ApplyRadial so non-radial slots cost
    // nothing. Owns its own _Draw; the fraction it draws from is pushed every frame (authoritative), never self-ticked.
    private RadialCooldownOverlay? _radial;
    // True while the radial path is driving this slot, so the local-tick _Process cooldown path stays disabled (the
    // two must not both render). Set in ApplyRadial; the radial overlay alone shows the countdown number then.
    private bool _radialActive;

    // Normal vs selected (amber) frame styles, built programmatically — the frame art is not imported yet, so we
    // use StyleBoxFlat borders; art can replace these by swapping the panel theme override later.
    private StyleBoxFlat? _frameNormal;
    private StyleBoxFlat? _frameSelected;

    // Local cooldown timer (seconds remaining). Seeded from HudState.Cooldowns via Apply(); ticked down here in
    // _Process so the countdown is smooth and client-only. <= 0 means the slot is ready (overlay hidden).
    private float _cooldownRemaining;

    // The last cooldown START value HudState reported. The HUD pushes the SAME start value every (throttled) frame
    // while a cooldown is "active" in the stub, so we only (re)seed our local timer when this value CHANGES — i.e.
    // a fresh cast / a new stub preset — otherwise the repeated identical pushes would re-extend the timer forever.
    private float _lastSeededCooldown;

    // The slot id this button represents ("1","2","Q","E","F","R","LMB","RMB"). Used as the Cooldowns/Counts key.
    public string SlotId { get; private set; } = string.Empty;

    public override void _Ready()
    {
        _icon = GetNodeOrNull<TextureRect>("Icon");
        _frame = GetNodeOrNull<Panel>("Frame");
        _keybind = GetNodeOrNull<Label>("Keybind");
        _count = GetNodeOrNull<Label>("Count");
        _cooldownShade = GetNodeOrNull<ColorRect>("CooldownShade");
        _cooldownText = GetNodeOrNull<Label>("CooldownShade/CooldownText");

        BuildFrameStyles();
        SetSelected(false);

        if (_count is not null)
        {
            _count.Visible = false;
        }

        if (_cooldownShade is not null)
        {
            _cooldownShade.Visible = false;
        }
    }

    // Static per-slot setup, called once after instancing: which icon to show, the keybind glyph beneath, and the
    // slot id used as the HudState dictionary key. Safe to call before _Ready (Hud calls it after AddChild, so the
    // nodes exist); we null-guard regardless.
    public void Configure(string slotId, Texture2D? icon, string keybind)
    {
        SlotId = slotId;
        if (_icon is null)
        {
            // _Ready may not have run yet when Hud configures right after AddChild — resolve the nodes now.
            _icon = GetNodeOrNull<TextureRect>("Icon");
            _keybind = GetNodeOrNull<Label>("Keybind");
        }

        if (_icon is not null)
        {
            _icon.Texture = icon;
        }

        if (_keybind is not null)
        {
            _keybind.Text = keybind;
        }
    }

    // Per-frame live update from the HUD: stack count (-1 hides the badge), selected (amber) frame, and the
    // cooldown START value. The HUD pushes the same start value every throttled frame, so we (re)seed our local
    // countdown ONLY when that start value CHANGES (a fresh cast / new stub preset); between changes the local
    // _Process tick owns the smooth countdown to zero. A change to a positive value restarts the sweep; a change
    // to 0 (cleared) lets the running timer finish on its own — we don't yank it to ready mid-countdown.
    public void Apply(int count, bool selected, float cooldownSeconds)
    {
        if (_count is not null)
        {
            if (count >= 0)
            {
                _count.Text = count.ToString();
                _count.Visible = true;
            }
            else
            {
                _count.Visible = false;
            }
        }

        SetSelected(selected);

        if (cooldownSeconds != _lastSeededCooldown)
        {
            _lastSeededCooldown = cooldownSeconds;
            if (cooldownSeconds > 0f)
            {
                _cooldownRemaining = cooldownSeconds;
            }
        }
    }

    // COMBAT-TUNING (radial cooldown): drive this slot's cooldown from a REAL, authoritative remaining fraction
    // (0..1) pushed EVERY frame, plus the remaining seconds for the centered countdown number. Unlike Apply's local
    // tick, this neither seeds nor ticks a timer — it renders the pushed fraction directly as a pie-slice sweep, so it
    // tracks the server cooldown (and a live combat.attackCooldownMs change) exactly. A fraction <= 0 clears the
    // overlay (ready). Lazily creates the overlay on first use so non-radial slots stay free.
    public void ApplyRadial(float fraction, float remainingSeconds)
    {
        _radialActive = true;
        // Disable the local-tick overlay (the radial path owns the visuals); guard against both rendering at once.
        _cooldownRemaining = 0f;
        if (_cooldownShade is not null)
        {
            // The radial overlay draws its own sweep + number; keep the full-rect darken shade hidden.
            _cooldownShade.Visible = false;
        }

        if (_radial is null)
        {
            _radial = new RadialCooldownOverlay { Name = "RadialCooldown" };
            // Cover ONLY the square icon, not the keybind label beneath: parent the overlay to the icon and fill
            // it (so FullRect == the square). Ignore mouse so clicks pass to the world/slot beneath.
            _radial.SetAnchorsPreset(LayoutPreset.FullRect);
            _radial.MouseFilter = MouseFilterEnum.Ignore;
            (_icon is not null ? (Control)_icon : this).AddChild(_radial);
        }

        // The overlay owns both the pie-slice sweep AND the centered whole-second countdown number. The icon is
        // left at FULL colour on purpose — only the radial sweep darkens the slot, the whole icon does not grey.
        _radial.Set(fraction, remainingSeconds);
    }

    public override void _Process(double delta)
    {
        // COMBAT-TUNING: when the radial (authoritative-fraction) path drives this slot, skip the local-tick cooldown
        // entirely — ApplyRadial already pushed the fraction this frame and the RadialCooldownOverlay draws both the
        // sweep and its own countdown number. The two paths must never render at once.
        if (_radialActive)
        {
            return;
        }

        if (_cooldownRemaining <= 0f)
        {
            if (_cooldownShade is not null && _cooldownShade.Visible)
            {
                _cooldownShade.Visible = false;
                if (_icon is not null)
                {
                    _icon.Modulate = Colors.White;
                }
            }

            return;
        }

        _cooldownRemaining -= (float)delta;
        if (_cooldownRemaining < 0f)
        {
            _cooldownRemaining = 0f;
        }

        if (_cooldownShade is not null)
        {
            _cooldownShade.Visible = true;
        }

        if (_icon is not null)
        {
            // Darken the icon while on cooldown so the overlay reads as "not ready".
            _icon.Modulate = new Color(0.45f, 0.45f, 0.45f, 1f);
        }

        if (_cooldownText is not null)
        {
            // Whole-second countdown, ceiling so it shows "1" through the final second and only blanks at ready.
            _cooldownText.Text = Mathf.CeilToInt(_cooldownRemaining).ToString();
        }
    }

    private void SetSelected(bool selected)
    {
        if (_frame is null)
        {
            return;
        }

        _frame.AddThemeStyleboxOverride("panel", selected ? _frameSelected : _frameNormal);
    }

    private void BuildFrameStyles()
    {
        _frameNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.14f, 0.55f),
            BorderColor = new Color(0.45f, 0.47f, 0.55f, 0.9f),
        };
        _frameNormal.SetBorderWidthAll(2);
        _frameNormal.SetCornerRadiusAll(6);

        // Amber "selected" frame (mockup: the Ultimate/R slot is highlighted).
        _frameSelected = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.13f, 0.04f, 0.55f),
            BorderColor = new Color(0.98f, 0.74f, 0.18f, 1f),
        };
        _frameSelected.SetBorderWidthAll(3);
        _frameSelected.SetCornerRadiusAll(6);
    }
}
