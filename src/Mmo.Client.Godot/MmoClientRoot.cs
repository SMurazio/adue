using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

public partial class MmoClientRoot : Node3D
{
    private readonly Dictionary<uint, MeshInstance3D> _entityNodes = [];
    private readonly Dictionary<uint, Label3D> _entityLabels = [];
    private readonly HashSet<TileCoord> _renderedBlockedTiles = [];
    private readonly Dictionary<Direction8, double> _nextStepAt = [];
    private readonly List<EntityRenderState> _renderStates = [];
    private readonly HashSet<uint> _seenEntityIds = [];
    private readonly List<uint> _staleEntityIds = [];
    private readonly List<string> _chatRows = [];
    private readonly StringBuilder _perfText = new(768);

    private MmoClient? _client;
    private Node3D? _worldRoot;
    private Node3D? _wallRoot;
    private Node3D? _entityRoot;
    private Camera3D? _camera;
    private Label? _statusLabel;
    private Label? _metricsLabel;
    private Label? _chatLabel;
    private LineEdit? _chatInput;
    private Label? _perfLabel;
    private FrameTimeGraph? _perfGraph;
    private double _elapsedSeconds;
    private double _nextMetricsAt;
    private double _nextOverlayAt;
    private double _nextPerfHudAt;
    private double _lastFrameMs;
    private double _maxFrameMs;
    private long _frameHitchCount;
    private long _clientGc0Count;
    private long _clientGc1Count;
    private long _clientGc2Count;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private bool _zoneBuilt;
    private bool _sentStartupChat;
    private bool _perfHudVisible;

    [Export] public string Host { get; set; } = ReadString("MMO_HOST", "127.0.0.1");
    [Export] public int Port { get; set; } = ReadInt("MMO_PORT", 7777);
    [Export] public string ConnectionKey { get; set; } = ReadString("MMO_CONNECTION_KEY", "local-dev");
    [Export] public string PlayerName { get; set; } = ReadString("MMO_PLAYER_NAME", $"Godot{Random.Shared.Next(1000, 9999)}");
    [Export] public float CameraSize { get; set; } = 28f;
    [Export] public double FrameHitchThresholdMs { get; set; } = ReadDouble("MMO_GODOT_FRAME_HITCH_MS", 33.3d);

    public override void _Ready()
    {
        BuildSceneShell();
        BuildOverlay();
        _lastGc0 = GC.CollectionCount(0);
        _lastGc1 = GC.CollectionCount(1);
        _lastGc2 = GC.CollectionCount(2);
        _client = new MmoClient(new ClientConnectionOptions(Host, Port, ConnectionKey, PlayerName, PlayerName, "mmo-godot-client"));
        _client.Connect();
        GD.Print($"Godot MMO client connecting to {Host}:{Port} as {PlayerName}.");
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += delta;
        var now = TimeSpan.FromSeconds(_elapsedSeconds);
        SampleFrameTiming(delta);
        _client?.Poll(now);

        if (_client?.Zone is not null && !_zoneBuilt)
        {
            BuildZone(_client.Zone);
            _zoneBuilt = true;
        }

        SendHeldMovement(now);
        SendStartupChat();
        RequestMetrics(now);
        SampleRenderStates(now);
        UpdateEntities();
        UpdateCamera();
        UpdateOverlay(now);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (key.Keycode == Key.F3)
        {
            TogglePerfHud();
            GetViewport().SetInputAsHandled();
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
        AddChild(_camera);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
    }

    private void BuildOverlay()
    {
        var layer = new CanvasLayer { Name = "Overlay" };
        AddChild(layer);

        _statusLabel = CreateOverlayLabel("Status", new Vector2(12, 10), new Vector2(760, 124), 15);
        _metricsLabel = CreateOverlayLabel("Metrics", new Vector2(0, 10), new Vector2(650, 330), 13);
        _chatLabel = CreateOverlayLabel("Chat", new Vector2(12, 0), new Vector2(760, 164), 14);
        _perfLabel = CreateOverlayLabel("PerfHud", new Vector2(12, 138), new Vector2(460, 184), 13);
        _perfGraph = new FrameTimeGraph
        {
            Name = "PerfFrameGraph",
            Position = new Vector2(12, 326),
            Size = new Vector2(460, 78),
            Visible = false
        };
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

        _perfLabel.Visible = false;

        layer.AddChild(_statusLabel);
        layer.AddChild(_metricsLabel);
        layer.AddChild(_chatLabel);
        layer.AddChild(_perfLabel);
        layer.AddChild(_perfGraph);
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

    private void SampleRenderStates(TimeSpan now)
    {
        _renderStates.Clear();
        _client?.CopyRenderStatesTo(_renderStates, now);
    }

    private void SampleFrameTiming(double delta)
    {
        var currentGc0 = GC.CollectionCount(0);
        var currentGc1 = GC.CollectionCount(1);
        var currentGc2 = GC.CollectionCount(2);
        var gc0 = currentGc0 - _lastGc0;
        var gc1 = currentGc1 - _lastGc1;
        var gc2 = currentGc2 - _lastGc2;
        _lastGc0 = currentGc0;
        _lastGc1 = currentGc1;
        _lastGc2 = currentGc2;
        _clientGc0Count += gc0;
        _clientGc1Count += gc1;
        _clientGc2Count += gc2;

        _lastFrameMs = Math.Max(0, delta * 1000d);
        _maxFrameMs = Math.Max(_maxFrameMs, _lastFrameMs);
        _perfGraph?.AddSample(_lastFrameMs);
        if (_lastFrameMs < FrameHitchThresholdMs)
        {
            return;
        }

        _frameHitchCount++;
        _client?.RecordFrameHitch(_lastFrameMs, gc0, gc1, gc2);
    }

    private void UpdateEntities()
    {
        if (_entityRoot is null)
        {
            return;
        }

        _seenEntityIds.Clear();
        foreach (var state in _renderStates)
        {
            _seenEntityIds.Add(state.NetworkId);
            if (!_entityNodes.TryGetValue(state.NetworkId, out var node))
            {
                node = CreateEntityNode(state);
                _entityNodes[state.NetworkId] = node;
                _entityRoot.AddChild(node);
            }

            node.Position = new Vector3((float)state.Position.X, 0f, (float)state.Position.Y);
            if (_entityLabels.TryGetValue(state.NetworkId, out var label))
            {
                SetTextIfChanged(label, state.DisplayName);
            }
        }

        _staleEntityIds.Clear();
        foreach (var networkId in _entityNodes.Keys)
        {
            if (!_seenEntityIds.Contains(networkId))
            {
                _staleEntityIds.Add(networkId);
            }
        }

        foreach (var stale in _staleEntityIds)
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

        EntityRenderState? local = null;
        foreach (var state in _renderStates)
        {
            if (state.NetworkId == localNetworkId)
            {
                local = state;
                break;
            }
        }

        if (local is null)
        {
            return;
        }

        var focus = new Vector3((float)local.Position.X, 0, (float)local.Position.Y);
        _camera.Position = focus + new Vector3(24, 28, 24);
        _camera.LookAt(focus, Vector3.Up);
    }

    private void UpdateOverlay(TimeSpan now)
    {
        UpdatePerfHud(now);

        if (_client is null)
        {
            return;
        }

        if (now.TotalSeconds < _nextOverlayAt)
        {
            return;
        }

        _nextOverlayAt = now.TotalSeconds + 0.1d;

        if (_statusLabel is not null)
        {
            var localTile = _client.LocalTile?.ToString() ?? "(unknown)";
            var server = _client.Server is null
                ? "server: pending"
                : $"server: v{_client.Server.ProtocolVersion}, tick={_client.Server.TickRate}Hz, step={_client.Server.StepCooldownMs}ms, aoi={_client.Server.InterestRadiusTiles:0.#}";
            var movementDebug = _client.DebugMovementEnabled
                ? "\n" + FormatMovementDebug(_client.MovementDebug) + "\n" + FormatFrameDebug()
                : "";
            SetTextIfChanged(_statusLabel,
                $"STATE {PlayerName}  {_client.State}  role={_client.Role}  visible={_client.EntityCount}  local={localTile}\n" +
                $"{server}\n" +
                "WASD is screen-relative. W=up, D=right, S+D=down-right. Enter/T opens chat. F3 toggles perf." +
                movementDebug);
        }

        if (_metricsLabel is not null)
        {
            SetTextIfChanged(_metricsLabel, FormatMetrics(_client));
        }

        if (_chatLabel is not null)
        {
            SetTextIfChanged(_chatLabel, FormatChat(_client));
        }
    }

    private void TogglePerfHud()
    {
        _perfHudVisible = !_perfHudVisible;
        if (_perfLabel is not null)
        {
            _perfLabel.Visible = _perfHudVisible;
        }

        if (_perfGraph is not null)
        {
            _perfGraph.Visible = _perfHudVisible;
        }

        _nextPerfHudAt = 0;
    }

    private void UpdatePerfHud(TimeSpan now)
    {
        if (!_perfHudVisible || _perfLabel is null)
        {
            return;
        }

        if (now.TotalSeconds < _nextPerfHudAt)
        {
            return;
        }

        _nextPerfHudAt = now.TotalSeconds + 0.1d;
        _perfText.Clear();
        _perfText.AppendLine("PERF HUD (F3)");
        AppendPerfRow("fps", Performance.GetMonitor(Performance.Monitor.TimeFps));
        AppendPerfRow("frame ms last/max", _lastFrameMs, _maxFrameMs);
        AppendPerfRow("process/physics ms",
            Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000d,
            Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000d);
        AppendPerfRow("draw/objects",
            Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame));
        AppendPerfRow("primitives",
            Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
        AppendPerfRow("video/static MB",
            BytesToMiB(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed)),
            BytesToMiB(Performance.GetMonitor(Performance.Monitor.MemoryStatic)));
        AppendPerfRow("managed MB", BytesToMiB(GC.GetTotalMemory(false)));
        AppendPerfRow("nodes", Performance.GetMonitor(Performance.Monitor.ObjectNodeCount));
        _perfText.Append("gc ")
            .Append(_clientGc0Count.ToString(CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_clientGc1Count.ToString(CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_clientGc2Count.ToString(CultureInfo.InvariantCulture))
            .Append("  hitches ")
            .Append(_frameHitchCount.ToString(CultureInfo.InvariantCulture))
            .Append(" >")
            .Append(FrameHitchThresholdMs.ToString("0.0", CultureInfo.InvariantCulture))
            .AppendLine("ms");

        SetTextIfChanged(_perfLabel, _perfText.ToString());
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

    private static void SetTextIfChanged(Label label, string value)
    {
        if (!string.Equals(label.Text, value, StringComparison.Ordinal))
        {
            label.Text = value;
        }
    }

    private static void SetTextIfChanged(Label3D label, string value)
    {
        if (!string.Equals(label.Text, value, StringComparison.Ordinal))
        {
            label.Text = value;
        }
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

    private string FormatChat(MmoClient client)
    {
        _chatRows.Clear();
        var chatLog = client.ChatLog;
        for (var i = chatLog.Count - 1; i >= 0 && _chatRows.Count < 8; i--)
        {
            var line = chatLog[i];
            if (IsMetricsLine(line.Text) || IsCommandDenied(line.Text))
            {
                continue;
            }

            _chatRows.Add($"{line.Sender}: {line.Text}");
        }

        _chatRows.Reverse();
        var errors = client.Errors;
        var firstError = Math.Max(0, errors.Count - 3);
        for (var i = firstError; i < errors.Count; i++)
        {
            var error = errors[i];
            _chatRows.Add($"error/{error.Code}: {error.Message}");
        }

        return _chatRows.Count == 0
            ? "CHAT"
            : "CHAT\n" + string.Join('\n', _chatRows);
    }

    private static string FormatMovementDebug(MovementDebugSnapshot debug)
    {
        var sent = debug.LastSentDirection.HasValue
            ? $"{debug.LastSentDirection.Value}#{debug.LastSentSequence}"
            : "-";
        var confirmedTile = debug.LastConfirmedTile?.ToString() ?? "-";
        var render = $"{debug.RenderPosition.X.ToString("0.###", CultureInfo.InvariantCulture)},{debug.RenderPosition.Y.ToString("0.###", CultureInfo.InvariantCulture)}";
        return $"MOVE sent={sent} confirmedSeq={debug.LastConfirmedSnapshotSequence} tile={confirmedTile} q={debug.QueueDepth} cadence={debug.EffectiveCadenceMs:0.#}ms latency={debug.LastLatencyMs}ms render={render}";
    }

    private string FormatFrameDebug()
    {
        return $"FRAME ms={_lastFrameMs.ToString("0.0", CultureInfo.InvariantCulture)}/{_maxFrameMs.ToString("0.0", CultureInfo.InvariantCulture)} hitches={_frameHitchCount} threshold={FrameHitchThresholdMs.ToString("0.0", CultureInfo.InvariantCulture)} gc={_clientGc0Count}/{_clientGc1Count}/{_clientGc2Count}";
    }

    private void AppendPerfRow(string label, double value)
    {
        _perfText.Append(label)
            .Append(' ')
            .Append(value.ToString("0.0", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private void AppendPerfRow(string label, double first, double second)
    {
        _perfText.Append(label)
            .Append(' ')
            .Append(first.ToString("0.0", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(second.ToString("0.0", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static double BytesToMiB(double bytes)
    {
        return bytes / (1024d * 1024d);
    }

    private static string LastMetric(MmoClient client, string prefix)
    {
        for (var i = client.ChatLog.Count - 1; i >= 0; i--)
        {
            var line = client.ChatLog[i];
            if (line.Sender == "server" && line.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line.Text;
            }
        }

        return "";
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

    private static double ReadDouble(string key, double fallback)
    {
        return double.TryParse(System.Environment.GetEnvironmentVariable(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}
