using System;
using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.UI;

// LOOT P4c: the corpse loot window. Opens when the player interacts with a corpse (the server replies with the
// corpse's contents) and lists each rolled stack as a row — rarity-coloured name + quantity + a per-row "Take"
// button — plus a "Loot all" button and a close X. PRESENTATION ONLY, mirroring InventoryWindow: it reads the
// client-side mirror (MmoClient.CorpseLoot, version-guarded) handed in via SetContents, and raises events for the
// take / loot-all / close intents; MmoClientRoot translates those into the MmoClient.SendLootItem/SendLootAll/
// SendCloseLoot calls. It never touches the protocol, the registry, or the corpse state directly.
//
// Mounted on the Hud CanvasLayer (Hud.MountLootWindow), hidden by default. The server drives open/close: a
// CorpseContents(Open=true) shows + fills it, a CorpseContents(Open=false) (emptied / decayed / out of range /
// despawned) clears it. The close X and Escape send a close to the server.
public partial class LootWindow : Control
{
    // Rarity -> row text colour for the loot list (common→legendary). Matches the design's "legible rarity" goal:
    // grey common, green uncommon, blue rare, purple epic, orange legendary — the familiar ARPG ramp.
    private static readonly Dictionary<Rarity, Color> RarityColors = new()
    {
        [Rarity.Common] = new Color(0.82f, 0.84f, 0.90f),
        [Rarity.Uncommon] = new Color(0.36f, 0.80f, 0.36f),
        [Rarity.Rare] = new Color(0.36f, 0.62f, 1.00f),
        [Rarity.Epic] = new Color(0.72f, 0.40f, 0.95f),
        [Rarity.Legendary] = new Color(1.00f, 0.62f, 0.18f),
    };

    // Raised when the player clicks a row's "Take" (carries the template key), the "Loot all" button, or closes the
    // window (close X / Escape). MmoClientRoot subscribes and forwards to the MmoClient send methods.
    public event Action<string>? TakeItemRequested;
    public event Action? LootAllRequested;
    public event Action? CloseRequested;

    private VBoxContainer? _list;
    private Button? _lootAllButton;
    private Label? _emptyHint;

    // The corpse network id currently shown — surfaced so MmoClientRoot can pass it on the send calls (the server
    // guards a stale window against it). 0 when nothing is open.
    public uint CorpseNetworkId { get; private set; }

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        BuildWindow();
        Visible = false;
    }

    // Show + fill the window from the open corpse's mirrored rows. MmoClientRoot calls this (CorpseLootVersion-guarded)
    // with ClientCorpseLoot.ToRows(registry). Re-presentation only; rebuilding the row list each refresh keeps the
    // panel reflecting the live remaining contents after each take.
    public void SetContents(uint corpseNetworkId, IReadOnlyList<CorpseLootRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        CorpseNetworkId = corpseNetworkId;
        RenderRows(rows);
        Visible = true;
    }

    // Hide the window without raising CloseRequested (used when the server already closed it — emptied/decayed/range).
    public void HideWindow()
    {
        CorpseNetworkId = 0;
        Visible = false;
    }

    // Public close intent (Escape from MmoClientRoot, or the close X). Tells the server, then hides locally.
    public void RaiseCloseRequested()
    {
        CloseRequested?.Invoke();
        HideWindow();
    }

    private void RenderRows(IReadOnlyList<CorpseLootRow> rows)
    {
        if (_list is null)
        {
            return;
        }

        foreach (var child in _list.GetChildren())
        {
            child.QueueFree();
        }

        foreach (var row in rows)
        {
            _list.AddChild(BuildRow(row));
        }

        if (_emptyHint is not null)
        {
            _emptyHint.Visible = rows.Count == 0;
        }

        if (_lootAllButton is not null)
        {
            _lootAllButton.Disabled = rows.Count == 0;
        }
    }

    private Control BuildRow(CorpseLootRow row)
    {
        var line = new HBoxContainer { Name = "Row_" + row.TemplateKey };
        line.AddThemeConstantOverride("separation", 8);

        var name = new Label
        {
            Name = "Name",
            Text = row.DisplayName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.AddThemeFontSizeOverride("font_size", 15);
        name.AddThemeColorOverride("font_color", RarityColors.TryGetValue(row.Rarity, out var color) ? color : RarityColors[Rarity.Common]);
        line.AddChild(name);

        var count = new Label
        {
            Name = "Count",
            Text = "x" + row.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(48, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        count.AddThemeFontSizeOverride("font_size", 15);
        count.AddThemeColorOverride("font_color", new Color(0.88f, 0.90f, 0.96f));
        line.AddChild(count);

        var take = new Button
        {
            Name = "Take",
            Text = "Take",
            CustomMinimumSize = new Vector2(64, 30),
        };
        var capturedKey = row.TemplateKey;
        take.Pressed += () => TakeItemRequested?.Invoke(capturedKey);
        line.AddChild(take);

        return line;
    }

    private void BuildWindow()
    {
        // Fill the HUD layer but ignore mouse so only the inner panel captures clicks (same idiom as InventoryWindow).
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
            CustomMinimumSize = new Vector2(360, 320),
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
        // Offset to the RIGHT of centre so it doesn't sit on top of the inventory window when both are open.
        panel.OffsetLeft = 40f;
        panel.OffsetRight = 400f;
        panel.OffsetTop = -160f;
        panel.OffsetBottom = 160f;
        AddChild(panel);

        var outer = new VBoxContainer { Name = "Outer" };
        outer.AddThemeConstantOverride("separation", 8);
        panel.AddChild(outer);

        // Title bar: "Loot" + close X.
        var bar = new HBoxContainer { Name = "TitleBar" };
        var title = new Label
        {
            Name = "Title",
            Text = "Loot",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        bar.AddChild(title);

        var close = new Button { Name = "CloseButton", Text = "X", CustomMinimumSize = new Vector2(30, 30) };
        close.AddThemeFontSizeOverride("font_size", 16);
        close.Pressed += RaiseCloseRequested;
        bar.AddChild(close);
        outer.AddChild(bar);

        // The scrollable-ish item list (a handful of rows — no scroll needed this slice).
        _list = new VBoxContainer { Name = "Items", SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        outer.AddChild(_list);

        _emptyHint = new Label { Name = "EmptyHint", Text = "(empty)", Visible = false };
        _emptyHint.AddThemeFontSizeOverride("font_size", 13);
        _emptyHint.AddThemeColorOverride("font_color", new Color(0.65f, 0.68f, 0.78f));
        outer.AddChild(_emptyHint);

        // "Loot all" footer button.
        _lootAllButton = new Button
        {
            Name = "LootAll",
            Text = "Loot all",
            CustomMinimumSize = new Vector2(0, 36),
        };
        _lootAllButton.Pressed += () => LootAllRequested?.Invoke();
        outer.AddChild(_lootAllButton);
    }
}
