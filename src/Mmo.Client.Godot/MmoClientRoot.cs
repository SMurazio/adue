using System;
using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

public partial class MmoClientRoot : Node3D
{
    private readonly Dictionary<uint, MeshInstance3D> _entityNodes = [];
    private readonly Dictionary<uint, Label3D> _entityLabels = [];
    private readonly HashSet<TileCoord> _renderedBlockedTiles = [];
    private readonly Dictionary<Direction8, double> _nextStepAt = [];

    private MmoClient? _client;
    private Node3D? _worldRoot;
    private Node3D? _wallRoot;
    private Node3D? _entityRoot;
    private Camera3D? _camera;
    private Label? _statusLabel;
    private Label? _metricsLabel;
    private Label? _chatLabel;
    private LineEdit? _chatInput;
    private double _elapsedSeconds;
    private double _nextMetricsAt;
    private bool _zoneBuilt;
    private bool _sentStartupChat;

    [Export] public string Host { get; set; } = ReadString("MMO_HOST", "127.0.0.1");
    [Export] public int Port { get; set; } = ReadInt("MMO_PORT", 7777);
    [Export] public string ConnectionKey { get; set; } = ReadString("MMO_CONNECTION_KEY", "local-dev");
    [Export] public string PlayerName { get; set; } = ReadString("MMO_PLAYER_NAME", $"Godot{Random.Shared.Next(1000, 9999)}");
    [Export] public float CameraSize { get; set; } = 28f;

    public override void _Ready()
    {
        BuildSceneShell();
        BuildOverlay();
        _client = new MmoClient(new ClientConnectionOptions(Host, Port, ConnectionKey, PlayerName, PlayerName, "mmo-godot-client"));
        _client.Connect();
        GD.Print($"Godot MMO client connecting to {Host}:{Port} as {PlayerName}.");
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += delta;
        var now = TimeSpan.FromSeconds(_elapsedSeconds);
        _client?.Poll(now);

        if (_client?.Zone is not null && !_zoneBuilt)
        {
            BuildZone(_client.Zone);
            _zoneBuilt = true;
        }

        SendHeldMovement(now);
        SendStartupChat();
        RequestMetrics(now);
        UpdateEntities(now);
        UpdateCamera();
        UpdateOverlay();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (key.Keycode == Key.Escape && _chatInput?.HasFocus() == true)
        {
            _chatInput.ReleaseFocus();
            GetViewport().SetInputAsHandled();
            return;
        }

        if ((key.Keycode == Key.Enter || key.Keycode == Key.KpEnter || key.Keycode == Key.T)
            && _chatInput?.HasFocus() != true)
        {
            FocusChatInput();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree()
    {
        _client?.Dispose();
    }

    private void BuildSceneShell()
    {
        _worldRoot = new Node3D { Name = "World" };
        _wallRoot = new Node3D { Name = "Walls" };
        _entityRoot = new Node3D { Name = "Entities" };
        AddChild(_worldRoot);
        _worldRoot.AddChild(_wallRoot);
        _worldRoot.AddChild(_entityRoot);

        var light = new DirectionalLight3D
        {
            Name = "Sun",
            LightEnergy = 2.4f,
            RotationDegrees = new Vector3(-55, 35, 0)
        };
        AddChild(light);

        _camera = new Camera3D
        {
            Name = "Camera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = CameraSize,
            Position = new Vector3(24, 28, 24)
        };
        _camera.LookAt(Vector3.Zero, Vector3.Up);
        AddChild(_camera);
    }

    private void BuildOverlay()
    {
        var layer = new CanvasLayer { Name = "Overlay" };
        AddChild(layer);

        _statusLabel = CreateOverlayLabel("Status", new Vector2(12, 10), new Vector2(680, 94), 15);
        _metricsLabel = CreateOverlayLabel("Metrics", new Vector2(0, 10), new Vector2(650, 330), 13);
        _chatLabel = CreateOverlayLabel("Chat", new Vector2(12, 0), new Vector2(760, 164), 14);
        _chatInput = new LineEdit
        {
            Name = "ChatInput",
            PlaceholderText = "Enter/T to chat. Try: hello or /role",
            Size = new Vector2(760, 28)
        };
        _chatInput.AddThemeFontSizeOverride("font_size", 14);
        _chatInput.AddThemeColorOverride("font_color", new Color(0.94f, 0.98f, 1.0f));
        _chatInput.AddThemeColorOverride("font_placeholder_color", new Color(0.70f, 0.78f, 0.82f));
        _chatInput.TextSubmitted += OnChatSubmitted;

        _metricsLabel.AnchorLeft = 1f;
        _metricsLabel.AnchorRight = 1f;
        _metricsLabel.OffsetLeft = -662f;
        _metricsLabel.OffsetRight = -12f;

        _chatLabel.AnchorTop = 1f;
        _chatLabel.AnchorBottom = 1f;
        _chatLabel.OffsetTop = -212f;
        _chatLabel.OffsetBottom = -48f;

        _chatInput.AnchorTop = 1f;
        _chatInput.AnchorBottom = 1f;
        _chatInput.OffsetLeft = 12f;
        _chatInput.OffsetRight = 772f;
        _chatInput.OffsetTop = -40f;
        _chatInput.OffsetBottom = -12f;

        layer.AddChild(_statusLabel);
        layer.AddChild(_metricsLabel);
        layer.AddChild(_chatLabel);
        layer.AddChild(_chatInput);
    }

    private void BuildZone(ZoneModel zone)
    {
        if (_worldRoot is null || _wallRoot is null)
        {
            return;
        }

        var ground = new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new BoxMesh { Size = new Vector3(zone.Width, 0.04f, zone.Height) },
            Position = new Vector3(zone.Width / 2f - 0.5f, -0.04f, zone.Height / 2f - 0.5f),
            MaterialOverride = Material(new Color(0.08f, 0.12f, 0.13f))
        };
        _worldRoot.AddChild(ground);

        var grid = new MeshInstance3D
        {
            Name = "Grid",
            Mesh = BuildGridMesh(zone.Width, zone.Height),
            MaterialOverride = Material(new Color(0.22f, 0.42f, 0.48f, 0.55f))
        };
        _worldRoot.AddChild(grid);

        foreach (var tile in zone.BlockedTiles)
        {
            if (!_renderedBlockedTiles.Add(tile))
            {
                continue;
            }

            var wall = new MeshInstance3D
            {
                Name = $"Wall_{tile.X}_{tile.Y}",
                Mesh = new BoxMesh { Size = new Vector3(0.92f, 0.85f, 0.92f) },
                Position = TileToWorld(tile, 0.4f),
                MaterialOverride = Material(new Color(0.45f, 0.50f, 0.53f))
            };
            _wallRoot.AddChild(wall);
        }
    }

    private void UpdateEntities(TimeSpan now)
    {
        if (_client is null || _entityRoot is null)
        {
            return;
        }

        var seen = new HashSet<uint>();
        foreach (var state in _client.GetRenderStates(now))
        {
            seen.Add(state.NetworkId);
            if (!_entityNodes.TryGetValue(state.NetworkId, out var node))
            {
                node = CreateEntityNode(state);
                _entityNodes[state.NetworkId] = node;
                _entityRoot.AddChild(node);
            }

            node.Position = new Vector3((float)state.Position.X, 0f, (float)state.Position.Y);
            if (_entityLabels.TryGetValue(state.NetworkId, out var label))
            {
                label.Text = state.DisplayName;
            }
        }

        foreach (var stale in _entityNodes.Keys
            .Where(networkId => !seen.Contains(networkId))
            .ToArray())
        {
            _entityNodes[stale].QueueFree();
            _entityNodes.Remove(stale);
            _entityLabels.Remove(stale);
        }
    }

    private MeshInstance3D CreateEntityNode(EntityRenderState state)
    {
        var body = new MeshInstance3D
        {
            Name = $"Entity_{state.NetworkId}",
            Mesh = new CapsuleMesh { Radius = 0.28f, Height = 0.9f },
            MaterialOverride = Material(state.IsLocal ? new Color(0.22f, 0.70f, 1.0f) : new Color(0.94f, 0.68f, 0.22f))
        };

        var label = new Label3D
        {
            Name = "Name",
            Text = state.DisplayName,
            PixelSize = 0.025f,
            Position = new Vector3(0, 0.9f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled
        };
        body.AddChild(label);
        _entityLabels[state.NetworkId] = label;
        return body;
    }

    private void UpdateCamera()
    {
        if (_camera is null || _client?.LocalNetworkId is not uint localNetworkId)
        {
            return;
        }

        var local = _client.GetRenderStates(TimeSpan.FromSeconds(_elapsedSeconds))
            .FirstOrDefault(entity => entity.NetworkId == localNetworkId);
        if (local is null)
        {
            return;
        }

        var focus = new Vector3((float)local.Position.X, 0, (float)local.Position.Y);
        _camera.Position = focus + new Vector3(24, 28, 24);
        _camera.LookAt(focus, Vector3.Up);
    }

    private void UpdateOverlay()
    {
        if (_client is null)
        {
            return;
        }

        if (_statusLabel is not null)
        {
            var localTile = _client.LocalTile?.ToString() ?? "(unknown)";
            var server = _client.Server is null
                ? "server: pending"
                : $"server: v{_client.Server.ProtocolVersion}, tick={_client.Server.TickRate}Hz, step={_client.Server.StepCooldownMs}ms, aoi={_client.Server.InterestRadiusTiles:0.#}";
            _statusLabel.Text =
                $"STATE {PlayerName}  {_client.State}  role={_client.Role}  visible={_client.Entities.Count}  local={localTile}\n" +
                $"{server}\n" +
                "WASD is screen-relative. W=up, D=right, S+D=down-right. Enter/T opens chat.";
        }

        if (_metricsLabel is not null)
        {
            _metricsLabel.Text = FormatMetrics(_client);
        }

        if (_chatLabel is not null)
        {
            var chat = _client.ChatLog
                .Where(static line => !IsMetricsLine(line.Text) && !IsCommandDenied(line.Text))
                .TakeLast(8)
                .Select(static line => $"{line.Sender}: {line.Text}");
            var errors = _client.Errors
                .TakeLast(3)
                .Select(static error => $"error/{error.Code}: {error.Message}");
            _chatLabel.Text = "CHAT\n" + string.Join('\n', chat.Concat(errors));
        }
    }

    private void SendHeldMovement(TimeSpan now)
    {
        if (_client is null || !_client.IsLoggedIn || _chatInput?.HasFocus() == true)
        {
            return;
        }

        var direction = CurrentDirection();
        if (!direction.HasValue)
        {
            return;
        }

        var cadence = TimeSpan.FromMilliseconds(_client.Server?.EffectiveStepCadenceMs ?? 150d);
        if (_nextStepAt.TryGetValue(direction.Value, out var nextAt) && now.TotalSeconds < nextAt)
        {
            return;
        }

        _client.SendMoveStep(direction.Value);
        _nextStepAt[direction.Value] = (now + cadence).TotalSeconds;
    }

    private void RequestMetrics(TimeSpan now)
    {
        if (_client?.IsLoggedIn != true
            || _client.Role != ClientRole.Admin
            || now.TotalSeconds < _nextMetricsAt)
        {
            return;
        }

        _client.SendChat("/metrics");
        _nextMetricsAt = now.TotalSeconds + 1d;
    }

    private void SendStartupChat()
    {
        var startupChat = System.Environment.GetEnvironmentVariable("MMO_GODOT_STARTUP_CHAT");
        if (_sentStartupChat || string.IsNullOrWhiteSpace(startupChat) || _client?.IsLoggedIn != true)
        {
            return;
        }

        _client.SendChat(startupChat);
        _sentStartupChat = true;
    }

    private void FocusChatInput()
    {
        if (_chatInput is null)
        {
            return;
        }

        _chatInput.GrabFocus();
        _chatInput.CaretColumn = _chatInput.Text.Length;
    }

    private void OnChatSubmitted(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            _client?.SendChat(trimmed);
        }

        if (_chatInput is not null)
        {
            _chatInput.Text = "";
            _chatInput.ReleaseFocus();
        }
    }

    private static Direction8? CurrentDirection()
    {
        var x = (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0);
        var y = (Input.IsKeyPressed(Key.S) ? 1 : 0) - (Input.IsKeyPressed(Key.W) ? 1 : 0);
        return ScreenRelativeDirectionMapper.FromInputAxes(x, y);
    }

    private static Vector3 TileToWorld(TileCoord tile, float y = 0f)
    {
        return new Vector3(tile.X, y, tile.Y);
    }

    private static ImmediateMesh BuildGridMesh(int width, int height)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (var x = 0; x <= width; x++)
        {
            mesh.SurfaceAddVertex(new Vector3(x - 0.5f, 0.02f, -0.5f));
            mesh.SurfaceAddVertex(new Vector3(x - 0.5f, 0.02f, height - 0.5f));
        }

        for (var y = 0; y <= height; y++)
        {
            mesh.SurfaceAddVertex(new Vector3(-0.5f, 0.02f, y - 0.5f));
            mesh.SurfaceAddVertex(new Vector3(width - 0.5f, 0.02f, y - 0.5f));
        }

        mesh.SurfaceEnd();
        return mesh;
    }

    private static StandardMaterial3D Material(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.82f
        };
    }

    private static Label CreateOverlayLabel(string name, Vector2 position, Vector2 size, int fontSize)
    {
        var label = new Label
        {
            Name = name,
            Position = position,
            Size = size,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(0.90f, 0.96f, 1.0f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        label.AddThemeConstantOverride("shadow_offset_x", 2);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static bool IsMetricsLine(string text)
    {
        return text.StartsWith("metrics", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("message metrics", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandDenied(string text)
    {
        return text.StartsWith("command denied:", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMetrics(MmoClient client)
    {
        if (client.Role != ClientRole.Admin)
        {
            return "SERVER METRICS\nAdmin role required.";
        }

        var state = LastMetric(client, "metrics state:");
        var live = LastMetric(client, "metrics 5s:");
        var total = LastMetric(client, "metrics total:");
        var messages = LastMetric(client, "message metrics:");

        if (state.Length == 0 && live.Length == 0 && total.Length == 0)
        {
            return "SERVER METRICS\nwaiting for /metrics...";
        }

        var rows = new List<string> { "SERVER METRICS" };
        if (state.Length > 0)
        {
            rows.Add($"peers/players {Metric(state, "peers")}/{Metric(state, "players")}   tick {Metric(state, "tick")}   up {Metric(state, "uptime")}");
        }

        if (live.Length > 0)
        {
            rows.Add($"live tick/s {Metric(live, "tick/s")}   tick ms {Metric(live, "tickMs avg/max")}");
            rows.Add($"snap/s {Metric(live, "snap/s")}   visible {Metric(live, "visible avg/max")}");
            rows.Add($"traffic out {Metric(live, "out")}   in {Metric(live, "in")}");
            rows.Add($"faults send/bad/net/runtime {Metric(live, "sendFail/s")}/{Metric(live, "bad/s")}/{Metric(live, "netErr/s")}/{Metric(live, "runtimeFault/s")}");
        }

        if (total.Length > 0)
        {
            rows.Add($"total avg out {Metric(total, "outAvg")}   snapshots {Metric(total, "snapshots")}");
        }

        if (messages.Length > 0)
        {
            rows.Add(Truncate(messages.Replace("message metrics:", "messages:"), 112));
        }

        return string.Join('\n', rows);
    }

    private static string LastMetric(MmoClient client, string prefix)
    {
        return client.ChatLog
            .Where(line => line.Sender == "server" && line.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(static line => line.Text)
            .LastOrDefault() ?? "";
    }

    private static string Metric(string line, string key)
    {
        var marker = key + "=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "?";
        }

        start += marker.Length;
        var end = line.IndexOf(',', start);
        if (end < start)
        {
            end = line.Length;
        }

        return line[start..end].Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string ReadString(string key, string fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int ReadInt(string key, int fallback)
    {
        return int.TryParse(System.Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
    }
}
