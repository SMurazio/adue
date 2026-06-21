using System;
using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.UI;

// S111 (HUD slice 4): the toggleable centered Inventory window from the approved mockup — a title bar
// ("Inventory" + close X), a left sidebar of category tabs (Gathering / Building / Gear / Consumables), and
// a 6x3 (18-slot) grid showing each item as a placeholder slot (short name/initial + stack count). It REPLACES
// the old top-right text-label inventory panel (S39) in MmoClientRoot.
//
// PRESENTATION ONLY — this reads the SAME client-side inventory data the old panel used: MmoClientRoot pulls
// ClientInventory.ToOrderedRows(registry) (guarded on Inventory.Version) and hands the rows + registry to
// SetInventory() here. This window NEVER touches InventoryUpdate handling, _client.Inventory, ItemRegistry, or
// ToOrderedRows — it only re-presents what it is given. It is built programmatically (like Hud.cs's action bar)
// from the minimal .tscn root so the layout stays data-driven.
//
// Mounted on the Hud CanvasLayer (Hud.MountInventoryWindow), hidden by default; the "I" hotkey + the close X
// toggle visibility. No movement/snapshot/prediction state is read or written.
public partial class InventoryWindow : Control
{
    // The four mockup tabs, in sidebar order. Items are bucketed into these by their ItemDefinition.Category
    // (see CategoryToTab). Gathering is the default selected tab (and the catch-all for unmapped categories).
    private enum Tab
    {
        Gathering,
        Building,
        Gear,
        Consumables,
    }

    private const int GridColumns = 6;
    private const int GridRows = 3;
    private const int SlotCount = GridColumns * GridRows; // 18

    private static readonly (Tab Tab, string Label)[] TabOrder =
    {
        (Tab.Gathering, "Gathering"),
        (Tab.Building, "Building"),
        (Tab.Gear, "Gear"),
        (Tab.Consumables, "Consumables"),
    };

    // The currently rendered rows bucketed per tab. Rebuilt on each SetInventory; the grid then renders the
    // selected tab's bucket. Each entry carries only what the grid draws — re-presentation of the given rows.
    private readonly Dictionary<Tab, List<InventoryRow>> _byTab = new()
    {
        [Tab.Gathering] = new List<InventoryRow>(),
        [Tab.Building] = new List<InventoryRow>(),
        [Tab.Gear] = new List<InventoryRow>(),
        [Tab.Consumables] = new List<InventoryRow>(),
    };

    private readonly Dictionary<Tab, Button> _tabButtons = new();
    private readonly List<InventorySlot> _slots = new(SlotCount);

    private Tab _selectedTab = Tab.Gathering;
    private Label? _tabHint;

    public override void _Ready()
    {
        BuildWindow();
        RenderGrid();
    }

    // Show/hide the window. Bound to the "I" hotkey (via Hud) and used so the close X can hide it.
    public void Toggle()
    {
        Visible = !Visible;
    }

    public void Close()
    {
        Visible = false;
    }

    // Re-present the given inventory rows. MmoClientRoot calls this (Version-guarded) with the SAME
    // ToOrderedRows(registry) data the old text panel used. We bucket each row into a tab by its registry
    // category (read-only registry lookup) and re-render the selected tab. We do NOT mutate the inventory.
    public void SetInventory(IReadOnlyList<InventoryRow> rows, ItemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var bucket in _byTab.Values)
        {
            bucket.Clear();
        }

        foreach (var row in rows)
        {
            var tab = ResolveTab(row.TemplateKey, registry);
            _byTab[tab].Add(row);
        }

        RenderGrid();
    }

    // Map an item to a tab via its registry category. The category enum currently only defines Resource
    // (wood/stone/fiber), which maps to Gathering. Items not in the registry, or with a category that does not
    // map to one of the four tabs, fall back to Gathering (the closest catch-all) — flagged for the orchestrator.
    private static Tab ResolveTab(string templateKey, ItemRegistry registry)
    {
        if (registry.TryGet(templateKey, out var definition))
        {
            return CategoryToTab(definition.Category);
        }

        // Unknown key (present in inventory but not the registry) — keep it visible under Gathering.
        return Tab.Gathering;
    }

    private static Tab CategoryToTab(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.Resource => Tab.Gathering,
            // TODO(content): no other ItemCategory values exist yet. When Building/Gear/Consumables item
            // categories are added to ItemCategory, extend this mapping. Unmapped categories fall to Gathering.
            _ => Tab.Gathering,
        };
    }

    private void BuildWindow()
    {
        // Centre the whole window on screen; this Control fills the HUD layer (full-rect from the .tscn) and
        // ignores mouse so only the inner panel captures clicks.
        MouseFilter = MouseFilterEnum.Ignore;

        var panel = new PanelContainer
        {
            Name = "WindowPanel",
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
            CustomMinimumSize = new Vector2(560, 380),
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.11f, 0.96f),
            BorderColor = new Color(0.40f, 0.43f, 0.52f, 1f),
        };
        panelStyle.SetBorderWidthAll(2);
        panelStyle.SetCornerRadiusAll(8);
        panelStyle.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        // Centre the fixed-size panel: offsets = -half size around the 0.5 anchors.
        panel.OffsetLeft = -280f;
        panel.OffsetRight = 280f;
        panel.OffsetTop = -190f;
        panel.OffsetBottom = 190f;
        AddChild(panel);

        var outer = new VBoxContainer { Name = "Outer" };
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);

        outer.AddChild(BuildTitleBar());

        var body = new HBoxContainer { Name = "Body", SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        body.AddChild(BuildTabSidebar());
        body.AddChild(BuildGridColumn());
        outer.AddChild(body);
    }

    private Control BuildTitleBar()
    {
        var bar = new HBoxContainer { Name = "TitleBar" };

        var title = new Label
        {
            Name = "Title",
            Text = "Inventory",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        bar.AddChild(title);

        var close = new Button
        {
            Name = "CloseButton",
            Text = "X",
            CustomMinimumSize = new Vector2(30, 30),
        };
        close.AddThemeFontSizeOverride("font_size", 16);
        close.Pressed += Close;
        bar.AddChild(close);

        return bar;
    }

    private Control BuildTabSidebar()
    {
        var sidebar = new VBoxContainer { Name = "Tabs", CustomMinimumSize = new Vector2(130, 0) };
        sidebar.AddThemeConstantOverride("separation", 6);

        foreach (var (tab, label) in TabOrder)
        {
            var button = new Button
            {
                Name = "Tab_" + tab,
                Text = label,
                ToggleMode = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 36),
            };
            var captured = tab;
            button.Pressed += () => SelectTab(captured);
            _tabButtons[tab] = button;
            sidebar.AddChild(button);
        }

        // A small note under the tabs to surface "showing first 18 / N hidden" without scrolling this slice.
        _tabHint = new Label { Name = "TabHint" };
        _tabHint.AddThemeFontSizeOverride("font_size", 11);
        _tabHint.AddThemeColorOverride("font_color", new Color(0.65f, 0.68f, 0.78f));
        _tabHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        sidebar.AddChild(_tabHint);

        return sidebar;
    }

    private Control BuildGridColumn()
    {
        var grid = new GridContainer
        {
            Name = "Grid",
            Columns = GridColumns,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);

        for (var i = 0; i < SlotCount; i++)
        {
            var slot = new InventorySlot();
            _slots.Add(slot);
            grid.AddChild(slot);
        }

        return grid;
    }

    private void SelectTab(Tab tab)
    {
        _selectedTab = tab;
        RenderGrid();
    }

    // Paint the 18 slots from the selected tab's bucket: filled slots show the placeholder + count, the rest
    // render as empty frames. If the bucket exceeds 18 we show the first 18 and note the overflow (no scrolling).
    private void RenderGrid()
    {
        // Keep the tab toggle visuals in sync with the selection.
        foreach (var (tab, button) in _tabButtons)
        {
            button.SetPressedNoSignal(tab == _selectedTab);
        }

        var bucket = _byTab.TryGetValue(_selectedTab, out var rows) ? rows : new List<InventoryRow>();

        for (var i = 0; i < _slots.Count; i++)
        {
            if (i < bucket.Count)
            {
                var row = bucket[i];
                _slots[i].ShowItem(row.DisplayName, row.Quantity);
            }
            else
            {
                _slots[i].ShowEmpty();
            }
        }

        if (_tabHint is not null)
        {
            if (bucket.Count > SlotCount)
            {
                _tabHint.Text = $"Showing first {SlotCount} of {bucket.Count}.";
            }
            else
            {
                _tabHint.Text = string.Empty;
            }
        }
    }
}

// One inventory grid cell: a framed slot that either renders an item placeholder (short name/initial + stack
// count) or an empty frame. There is NO per-item art yet, so the icon is a text placeholder — TODO(art) below.
// Kept as a small local control (not the action-bar SlotButton, whose keybind/cooldown machinery is unrelated).
internal sealed partial class InventorySlot : Control
{
    private Panel? _frame;
    private Label? _name;
    private Label? _count;

    private static readonly StyleBoxFlat EmptyStyle = MakeStyle(
        new Color(0.10f, 0.11f, 0.14f, 0.55f), new Color(0.32f, 0.34f, 0.42f, 0.8f));

    private static readonly StyleBoxFlat FilledStyle = MakeStyle(
        new Color(0.16f, 0.18f, 0.24f, 0.85f), new Color(0.52f, 0.56f, 0.66f, 1f));

    public InventorySlot()
    {
        CustomMinimumSize = new Vector2(60, 60);

        _frame = new Panel { Name = "Frame" };
        _frame.SetAnchorsPreset(LayoutPreset.FullRect);
        _frame.MouseFilter = MouseFilterEnum.Ignore;
        _frame.AddThemeStyleboxOverride("panel", EmptyStyle);
        AddChild(_frame);

        // TODO(art): per-item icons — once item art exists, drop a TextureRect here and hide/replace this label.
        _name = new Label
        {
            Name = "ItemName",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _name.SetAnchorsPreset(LayoutPreset.FullRect);
        _name.AddThemeFontSizeOverride("font_size", 12);
        _name.AddThemeColorOverride("font_color", new Color(0.90f, 0.92f, 1f));
        AddChild(_name);

        _count = new Label
        {
            Name = "Count",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _count.SetAnchorsPreset(LayoutPreset.FullRect);
        _count.OffsetRight = -3f;
        _count.OffsetBottom = -1f;
        _count.AddThemeFontSizeOverride("font_size", 13);
        _count.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        _count.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
        _count.AddThemeConstantOverride("outline_size", 4);
        AddChild(_count);

        ShowEmpty();
    }

    public void ShowItem(string displayName, int quantity)
    {
        _frame?.AddThemeStyleboxOverride("panel", FilledStyle);
        if (_name is not null)
        {
            _name.Text = ShortName(displayName);
            _name.Visible = true;
        }

        if (_count is not null)
        {
            _count.Text = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _count.Visible = true;
        }
    }

    public void ShowEmpty()
    {
        _frame?.AddThemeStyleboxOverride("panel", EmptyStyle);
        if (_name is not null)
        {
            _name.Text = string.Empty;
            _name.Visible = false;
        }

        if (_count is not null)
        {
            _count.Text = string.Empty;
            _count.Visible = false;
        }
    }

    // Placeholder label: show the full short-ish display name (it's tiny — "Wood"/"Stone"/"Fiber"). If a name is
    // long it autowraps; we cap it so the slot stays readable until real icons land.
    private static string ShortName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return "?";
        }

        return displayName.Length <= 8 ? displayName : displayName[..8];
    }

    private static StyleBoxFlat MakeStyle(Color bg, Color border)
    {
        var style = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }
}
