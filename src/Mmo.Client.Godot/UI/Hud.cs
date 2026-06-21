using System.Collections.Generic;
using Godot;

namespace Mmo.Client.Godot.UI;

// S107 (HUD slice 1) + S108 (slice 3): the root of the in-game HUD — a CanvasLayer SEPARATE from MmoClientRoot's
// "Overlay" (which owns the status/perf/admin panels). MmoClientRoot instantiates Hud.tscn, adds it as a child in
// _Ready (additive hook only), then pushes a HudState into it each frame via SetState.
//
// S108 builds the bottom-center ACTION BAR to match the approved mockup, left -> right: 2 consumable slots (keys
// 1/2 with stack badges), a circular character portrait (+ mount sub-badge + low-health red tint), the vitals
// (green health + blue resource bars), 2 autoattack slots (LMB/RMB), and 4 spell slots (Q/E/F/R, amber frame on
// the selected one). It is assembled programmatically here from the reusable SlotButton scene (UI/SlotButton.tscn)
// so the slots stay data-driven; the bar is anchored bottom-center and reads ONLY from HudState.
//
// Presentation-only: the HUD reads from HudState; it never touches movement/snapshot/prediction code.
public partial class Hud : CanvasLayer
{
    // res:// paths for the imported art (case-sensitive — see todo/S108). Loaded once in _Ready.
    private const string AbilityDir = "res://content/ui/icons/abilities/";
    private const string ConsumableDir = "res://content/ui/icons/consumables/";
    private const string PortraitDir = "res://content/ui/portraits/";

    // Slot ids must match the keys the F5 stub cycler (and a future ability system) writes into HudState.Cooldowns
    // / Counts. Consumables 1/2; autoattacks LMB/RMB; spells Q/E/F/R.
    private readonly Dictionary<string, SlotButton> _slots = new();

    private TextureRect? _portrait;
    private TextureRect? _mountBadge;
    private ColorRect? _healthFill;
    private ColorRect? _resourceFill;
    // COMBAT-S1: the third vitals bar — stamina (yellow). Same track+fill structure as health/resource.
    private ColorRect? _staminaFill;

    // S109: the top-right framed minimap (its own scene/controller). The HUD owns it as a child and pushes the
    // current HudState into it each Refresh; the minimap reads the local position/facing + static map from there.
    private Minimap? _minimap;

    // S111: the toggleable centered Inventory window (replaces the old top-right text panel). The HUD owns it as
    // a child; MmoClientRoot toggles it ("I") and feeds it the (Version-guarded) inventory rows. Hidden by default.
    private InventoryWindow? _inventory;

    // The portrait's base (white) modulate so we can toggle the low-health red tint without losing the texture.
    private static readonly Color PortraitNormalTint = Colors.White;
    private static readonly Color PortraitLowHealthTint = new(1f, 0.45f, 0.45f, 1f);

    // The full pixel width the vitals fill bars occupy at 100%; the fill ColorRects are scaled within this.
    private const float VitalsBarWidth = 220f;

    // The current view-model. MmoClientRoot owns the instance and feeds it each frame; the HUD only reads it.
    public HudState State { get; private set; } = new();

    public override void _Ready()
    {
        // High layer so the HUD draws above the 3D world; the existing Overlay sits at the default layer 1, so the
        // admin/debug panels stay usable.
        Layer = 2;
        BuildActionBar();
        MountMinimap();
        MountInventoryWindow();
    }

    // S111: instantiate the Inventory window scene and add it as a child of the HUD (hidden by default). Falls
    // back to a bare InventoryWindow if the scene fails to load so the HUD still renders. MmoClientRoot drives it
    // via ToggleInventory() ("I" hotkey) and SetInventory() (the Version-guarded inventory refresh).
    private void MountInventoryWindow()
    {
        var scene = GD.Load<PackedScene>("res://UI/InventoryWindow.tscn");
        _inventory = scene?.Instantiate<InventoryWindow>() ?? new InventoryWindow();
        _inventory.Visible = false;
        AddChild(_inventory);
    }

    // S111: open/close the Inventory window. Bound to the "I" hotkey in MmoClientRoot.
    public void ToggleInventory()
    {
        _inventory?.Toggle();
    }

    // S111: re-present the current inventory in the window. MmoClientRoot calls this from its Version-guarded
    // UpdateInventory() with the SAME ToOrderedRows(registry) data the old text panel used — presentation only.
    public void SetInventory(System.Collections.Generic.IReadOnlyList<Mmo.Client.Core.InventoryRow> rows,
        Mmo.Shared.Domain.ItemRegistry registry)
    {
        _inventory?.SetInventory(rows, registry);
    }

    // S109: instantiate the top-right minimap scene and add it as a child of the HUD. Falls back to a bare Minimap
    // if the scene fails to load so the HUD still renders. The minimap self-anchors to the top-right in its _Ready.
    private void MountMinimap()
    {
        var scene = GD.Load<PackedScene>("res://UI/Minimap.tscn");
        _minimap = scene?.Instantiate<Minimap>() ?? new Minimap();
        AddChild(_minimap);
    }

    // Replace the view-model the HUD renders from. Called by MmoClientRoot's additive hook once per (throttled)
    // frame after it has refreshed the stub/real fields. The HUD only reads it.
    public void SetState(HudState state)
    {
        State = state;
        Refresh();
    }

    // Build the bottom-center action bar once. Layout is a horizontal row: [consumables] [portrait] [vitals over
    // autoattacks + spells]. Done programmatically so the SlotButton instancing stays data-driven; the visual
    // reads as the mockup arrangement (not pixel-perfect, per the task constraints).
    private void BuildActionBar()
    {
        // A bottom-centered container that hugs its content and sits above the bottom edge.
        var bar = new HBoxContainer
        {
            Name = "ActionBar",
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin,
            OffsetBottom = -16f,
            OffsetTop = -120f,
        };
        bar.AddThemeConstantOverride("separation", 14);
        // Center the HBox on the bottom edge: pivot via a left offset of half its (content) width is awkward for an
        // auto-sized HBox, so we let it grow both ways from the 0.5 anchor — Godot keeps it centered.
        bar.AddChild(BuildVerticalCenter(BuildConsumables()));
        bar.AddChild(BuildVerticalCenter(BuildPortrait()));
        bar.AddChild(BuildVerticalCenter(BuildVitalsAndAbilities()));
        AddChild(bar);
    }

    // Wrap content in a CenterContainer-ish VBox so groups of different heights sit bottom-aligned together.
    private static Control BuildVerticalCenter(Control content)
    {
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        box.AddChild(content);
        return box;
    }

    private Control BuildConsumables()
    {
        var row = new HBoxContainer { Name = "Consumables" };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(MakeSlot("1", ConsumableDir + "Health_potion.png", "1"));
        row.AddChild(MakeSlot("2", ConsumableDir + "Resource_potion.png", "2"));
        return row;
    }

    private Control BuildPortrait()
    {
        // A fixed-size container holding the circular portrait + an overlaid mount sub-badge at bottom-right.
        var holder = new Control { Name = "Portrait", CustomMinimumSize = new Vector2(96, 96) };

        _portrait = new TextureRect
        {
            Name = "PortraitImage",
            Texture = Load(PortraitDir + "Character_portrait.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            OffsetRight = 96f,
            OffsetBottom = 96f,
        };
        // Circular look: a corner-radius clip via a clipping panel is over-engineering for this slice; instead we
        // rely on the portrait art already reading round and round the frame off. A simple circular StyleBox mask
        // would need a shader — the task says a simple approach is fine, so we leave the square texture but draw a
        // round border ring around it so it reads as the mockup's circular portrait.
        holder.AddChild(_portrait);

        var ring = new Panel { Name = "PortraitRing", OffsetRight = 96f, OffsetBottom = 96f };
        var ringStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = new Color(0.45f, 0.47f, 0.55f, 0.95f),
        };
        ringStyle.SetBorderWidthAll(3);
        ringStyle.SetCornerRadiusAll(48); // half of 96 -> full circle
        ring.AddThemeStyleboxOverride("panel", ringStyle);
        ring.MouseFilter = Control.MouseFilterEnum.Ignore;
        holder.AddChild(ring);

        _mountBadge = new TextureRect
        {
            Name = "MountBadge",
            Texture = Load(PortraitDir + "mount_portrait.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            OffsetLeft = 58f,
            OffsetTop = 58f,
            OffsetRight = 96f,
            OffsetBottom = 96f,
            Visible = false,
        };
        holder.AddChild(_mountBadge);

        return holder;
    }

    private Control BuildVitalsAndAbilities()
    {
        var col = new VBoxContainer { Name = "VitalsAndAbilities" };
        col.AddThemeConstantOverride("separation", 6);
        col.AddChild(BuildVitals());

        var abilities = new HBoxContainer { Name = "Abilities" };
        abilities.AddThemeConstantOverride("separation", 8);
        abilities.AddChild(MakeSlot("LMB", AbilityDir + "Auto_attack.png", "LMB"));
        abilities.AddChild(MakeSlot("RMB", AbilityDir + "Heavy_auto_attack.png", "RMB"));
        // A small gap, then the 4 spells.
        abilities.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
        abilities.AddChild(MakeSlot("Q", AbilityDir + "Simple_spell.png", "Q"));
        abilities.AddChild(MakeSlot("E", AbilityDir + "Advanced_spell.png", "E"));
        abilities.AddChild(MakeSlot("F", AbilityDir + "Defensive_spell.png", "F"));
        abilities.AddChild(MakeSlot("R", AbilityDir + "Ultimate_spell.png", "R"));
        col.AddChild(abilities);
        return col;
    }

    private Control BuildVitals()
    {
        var col = new VBoxContainer { Name = "Vitals" };
        col.AddThemeConstantOverride("separation", 4);
        col.AddChild(BuildVitalBar(out _healthFill, new Color(0.20f, 0.78f, 0.30f, 1f)));
        col.AddChild(BuildVitalBar(out _resourceFill, new Color(0.22f, 0.50f, 0.95f, 1f)));
        // COMBAT-S1: stamina bar (yellow), matching the existing track/fill look.
        col.AddChild(BuildVitalBar(out _staminaFill, new Color(0.92f, 0.82f, 0.20f, 1f)));
        return col;
    }

    // One vitals bar: a dark track ColorRect with a coloured fill ColorRect on top, width-scaled in Refresh.
    private static Control BuildVitalBar(out ColorRect fill, Color color)
    {
        var track = new Control { CustomMinimumSize = new Vector2(VitalsBarWidth, 14) };
        var bg = new ColorRect
        {
            Color = new Color(0.08f, 0.08f, 0.10f, 0.85f),
            OffsetRight = VitalsBarWidth,
            OffsetBottom = 14f,
        };
        track.AddChild(bg);

        fill = new ColorRect
        {
            Color = color,
            OffsetRight = VitalsBarWidth,
            OffsetBottom = 14f,
        };
        track.AddChild(fill);
        return track;
    }

    private SlotButton MakeSlot(string slotId, string iconPath, string keybind)
    {
        var scene = GD.Load<PackedScene>("res://UI/SlotButton.tscn");
        var slot = scene?.Instantiate<SlotButton>();
        if (slot is null)
        {
            // Fall back to a bare SlotButton so the bar still lays out if the scene fails to load.
            slot = new SlotButton();
        }

        slot.Name = "Slot_" + slotId;
        slot.Configure(slotId, Load(iconPath), keybind);
        _slots[slotId] = slot;
        return slot;
    }

    private static Texture2D? Load(string path)
    {
        var tex = GD.Load<Texture2D>(path);
        if (tex is null)
        {
            GD.PushWarning($"S108 HUD: texture failed to load: {path} (was the headless --import run?)");
        }

        return tex;
    }

    // Repaint from the current State: vitals fill ratios, portrait state (mount badge + low-health tint), and the
    // per-slot count / selected / cooldown values. Cooldown countdown itself runs locally in each SlotButton's
    // _Process; here we only push the START value from HudState.Cooldowns.
    private void Refresh()
    {
        if (_healthFill is not null)
        {
            // Width-scale the fill via OffsetRight (left stays at 0) so a 0%/stale Size.Y never collapses height.
            _healthFill.OffsetRight = VitalsBarWidth * Ratio(State.Health, State.MaxHealth);
        }

        if (_resourceFill is not null)
        {
            _resourceFill.OffsetRight = VitalsBarWidth * Ratio(State.Resource, State.MaxResource);
        }

        if (_staminaFill is not null)
        {
            _staminaFill.OffsetRight = VitalsBarWidth * Ratio(State.Stamina, State.MaxStamina);
        }

        ApplyPortrait();

        foreach (var (slotId, slot) in _slots)
        {
            var count = State.Counts.TryGetValue(slotId, out var c) ? c : -1;
            var selected = slotId == State.SelectedSlot;
            var cooldown = State.Cooldowns.TryGetValue(slotId, out var cd) ? cd : 0f;
            slot.Apply(count, selected, cooldown);
        }

        // S109: push the same view-model into the minimap (it bakes the static map once, then just moves the marker).
        _minimap?.Apply(State);
    }

    private void ApplyPortrait()
    {
        var portraitState = State.PortraitState();
        if (_mountBadge is not null)
        {
            _mountBadge.Visible = portraitState == HudState.Portrait.Mount;
        }

        if (_portrait is not null)
        {
            // TODO(art): low-health portrait — the dedicated worried-face/red-ring art is not provided yet, so we
            // tint the normal portrait red as a fallback. Swap to the real texture here when it lands (one-liner).
            _portrait.Modulate = portraitState == HudState.Portrait.LowHealth
                ? PortraitLowHealthTint
                : PortraitNormalTint;
        }
    }

    private static float Ratio(float value, float max)
    {
        if (max <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp(value / max, 0f, 1f);
    }
}
