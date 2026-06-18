using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

public partial class MmoClientRoot : Node3D, IControlHost
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
    private readonly BoxMesh _wallMesh = new() { Size = new Vector3(0.92f, 0.85f, 0.92f) };
    private readonly CapsuleMesh _entityMesh = new() { Radius = 0.28f, Height = 0.9f };
    private readonly StandardMaterial3D _groundMaterial = Material(new Color(0.08f, 0.12f, 0.13f));
    private readonly StandardMaterial3D _wallMaterial = Material(new Color(0.45f, 0.50f, 0.53f));
    private readonly StandardMaterial3D _localEntityMaterial = Material(new Color(0.22f, 0.70f, 1.0f));
    private readonly StandardMaterial3D _remoteEntityMaterial = Material(new Color(0.94f, 0.68f, 0.22f));

    private MmoClient? _client;
    private Node3D? _worldRoot;
    private Node3D? _wallRoot;
    private Node3D? _entityRoot;
    private Camera3D? _camera;
    private Label? _statusLabel;
    private Label? _metricsLabel;
    private Label? _chatLabel;
    private LineEdit? _chatInput;
    private PanelContainer? _perfPanel;
    private Label? _perfLabel;
    private FrameTimeGraph? _perfGraph;
    private double _elapsedSeconds;
    private double _nextMetricsAt;
    private double _nextOverlayAt;
    private double _nextPerfHudAt;
    private double _lastFrameMs;
    private double _maxFrameMs;
    private double _lastPollMs;
    private double _lastRenderStateMs;
    private double _lastEntitiesMs;
    private double _lastCameraMs;
    private double _lastOverlayMs;
    private double _maxPollMs;
    private double _maxRenderStateMs;
    private double _maxEntitiesMs;
    private double _maxCameraMs;
    private double _maxOverlayMs;
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

    // Debug control channel (T2). Null unless MMO_DEBUG_CONTROL_PORT is set; absent => zero behavior change.
    private DebugControlChannel? _controlChannel;

    // Injected movement: a direction held for a fixed duration, sent on the same cadence as real input.
    // _injectedSingleStep latches a one-shot step (move with durationMs<=0) that clears once it fires.
    private Direction8? _injectedDirection;
    private double _injectedUntilSeconds;
    private bool _injectedSingleStep;

    // Autopilot: a scripted movement loop that also streams per-frame telemetry to .run/client-frames.csv.
    private Direction8[]? _autopilotPattern;
    private double _autopilotEndsAtSeconds;
    private double _autopilotLegSeconds;
    private int _autopilotLegIndex;
    private StreamWriter? _frameCsv;
    private int _frameCsvGc0;
    private int _frameCsvGc1;
    private int _frameCsvGc2;

    // Per-_Process-section timing (ms), surfaced in the F3 HUD and read by the telemetry channel (T2).
    internal double LastPollMs => _lastPollMs;
    internal double LastRenderStateMs => _lastRenderStateMs;
    internal double LastEntitiesMs => _lastEntitiesMs;
    internal double LastCameraMs => _lastCameraMs;
    internal double LastOverlayMs => _lastOverlayMs;
    internal double MaxPollMs => _maxPollMs;
    internal double MaxRenderStateMs => _maxRenderStateMs;
    internal double MaxEntitiesMs => _maxEntitiesMs;
    internal double MaxCameraMs => _maxCameraMs;
    internal double MaxOverlayMs => _maxOverlayMs;

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
        // MMO_DEBUG_FRAME_LOG: dump per-frame telemetry to .run/client-frames-<player>.csv during
        // normal (human) play. No socket/listener, so no firewall prompt; the agent reads the file.
        if (!string.IsNullOrWhiteSpace(ReadString("MMO_DEBUG_FRAME_LOG", string.Empty)))
        {
            OpenFrameCsv();
        }
        _client = new MmoClient(new ClientConnectionOptions(Host, Port, ConnectionKey, PlayerName, PlayerName, "mmo-godot-client"));
        _client.Connect();
        GD.Print($"Godot MMO client connecting to {Host}:{Port} as {PlayerName}.");

        _controlChannel = DebugControlChannel.TryCreate(this);
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += delta;
        var now = TimeSpan.FromSeconds(_elapsedSeconds);
        SampleFrameTiming(delta);

        var tPoll0 = Time.GetTicksUsec();
        _client?.Poll(now);
        _controlChannel?.Poll();
        var pollUsec = Time.GetTicksUsec() - tPoll0;

        if (_client?.Zone is not null && !_zoneBuilt)
        {
            BuildZone(_client.Zone);
            _zoneBuilt = true;
        }

        AdvanceAutopilot(now);
        SendHeldMovement(now);
        SendStartupChat();
        RequestMetrics(now);

        var t0 = Time.GetTicksUsec();
        SampleRenderStates(now);
        var t1 = Time.GetTicksUsec();
        UpdateEntities();
        var t2 = Time.GetTicksUsec();
        UpdateCamera();
        var t3 = Time.GetTicksUsec();
        UpdateOverlay(now);
        var t4 = Time.GetTicksUsec();

        RecordSectionTiming(pollUsec, t1 - t0, t2 - t1, t3 - t2, t4 - t3);
        AppendFrameCsvRow();
    }

    private void RecordSectionTiming(ulong pollUsec, ulong renderStateUsec, ulong entitiesUsec, ulong cameraUsec, ulong overlayUsec)
    {
        _lastPollMs = pollUsec / 1000d;
        _lastRenderStateMs = renderStateUsec / 1000d;
        _lastEntitiesMs = entitiesUsec / 1000d;
        _lastCameraMs = cameraUsec / 1000d;
        _lastOverlayMs = overlayUsec / 1000d;
        _maxPollMs = Math.Max(_maxPollMs, _lastPollMs);
        _maxRenderStateMs = Math.Max(_maxRenderStateMs, _lastRenderStateMs);
        _maxEntitiesMs = Math.Max(_maxEntitiesMs, _lastEntitiesMs);
        _maxCameraMs = Math.Max(_maxCameraMs, _lastCameraMs);
        _maxOverlayMs = Math.Max(_maxOverlayMs, _lastOverlayMs);
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

        if (key.Keycode == Key.F11)
        {
            var mode = DisplayServer.WindowGetMode();
            DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.ExclusiveFullscreen
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.ExclusiveFullscreen);
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
        _controlChannel?.Dispose();
        CloseFrameCsv();
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

        var statusPanel = CreateOverlayPanel("StatusPanel", new Vector2(12, 10), new Vector2(840, 132));
        var statusRows = CreatePanelVBox(statusPanel);
        _statusLabel = CreateOverlayLabel("Status", 15);
        statusRows.AddChild(_statusLabel);

        var metricsPanel = CreateOverlayPanel("MetricsPanel", Vector2.Zero, new Vector2(650, 330));
        metricsPanel.AnchorLeft = 1f;
        metricsPanel.AnchorRight = 1f;
        metricsPanel.OffsetLeft = -662f;
        metricsPanel.OffsetRight = -12f;
        metricsPanel.OffsetTop = 10f;
        metricsPanel.OffsetBottom = 340f;
        var metricsRows = CreatePanelVBox(metricsPanel);
        _metricsLabel = CreateOverlayLabel("Metrics", 13);
        metricsRows.AddChild(_metricsLabel);

        var chatPanel = CreateOverlayPanel("ChatPanel", Vector2.Zero, new Vector2(760, 164));
        chatPanel.AnchorTop = 1f;
        chatPanel.AnchorBottom = 1f;
        chatPanel.OffsetLeft = 12f;
        chatPanel.OffsetRight = 772f;
        chatPanel.OffsetTop = -220f;
        chatPanel.OffsetBottom = -50f;
        var chatRows = CreatePanelVBox(chatPanel);
        _chatLabel = CreateOverlayLabel("Chat", 14);
        chatRows.AddChild(_chatLabel);

        _perfPanel = CreateOverlayPanel("PerfPanel", new Vector2(12, 154), new Vector2(460, 304));
        var perfRows = CreatePanelVBox(_perfPanel);
        _perfLabel = CreateOverlayLabel("PerfHud", 13);
        _perfGraph = new FrameTimeGraph
        {
            Name = "PerfFrameGraph",
            CustomMinimumSize = new Vector2(436, 78)
        };
        perfRows.AddChild(_perfLabel);
        perfRows.AddChild(_perfGraph);

        var inputPanel = CreateOverlayPanel("ChatInputPanel", Vector2.Zero, new Vector2(760, 40));
        inputPanel.AnchorTop = 1f;
        inputPanel.AnchorBottom = 1f;
        inputPanel.OffsetLeft = 12f;
        inputPanel.OffsetRight = 772f;
        inputPanel.OffsetTop = -42f;
        inputPanel.OffsetBottom = -8f;
        var inputMargin = CreatePanelMargin(inputPanel);
        _chatInput = new LineEdit
        {
            Name = "ChatInput",
            PlaceholderText = "Enter/T to chat. Try: hello or /role",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _chatInput.AddThemeFontSizeOverride("font_size", 14);
        _chatInput.AddThemeColorOverride("font_color", new Color(0.94f, 0.98f, 1.0f));
        _chatInput.AddThemeColorOverride("font_placeholder_color", new Color(0.70f, 0.78f, 0.82f));
        _chatInput.TextSubmitted += OnChatSubmitted;
        inputMargin.AddChild(_chatInput);

        _perfPanel.Visible = false;

        layer.AddChild(statusPanel);
        layer.AddChild(metricsPanel);
        layer.AddChild(chatPanel);
        layer.AddChild(_perfPanel);
        layer.AddChild(inputPanel);
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
            MaterialOverride = _groundMaterial
        };
        _worldRoot.AddChild(ground);

        var grid = new MeshInstance3D
        {
            Name = "Grid",
            Mesh = new PlaneMesh { Size = new Vector2(zone.Width, zone.Height) },
            Position = new Vector3(zone.Width / 2f - 0.5f, 0.02f, zone.Height / 2f - 0.5f),
            MaterialOverride = CreateGridMaterial()
        };
        _worldRoot.AddChild(grid);

        var wallTiles = new List<TileCoord>();
        foreach (var tile in zone.BlockedTiles)
        {
            if (!_renderedBlockedTiles.Add(tile))
            {
                continue;
            }

            wallTiles.Add(tile);
        }

        if (wallTiles.Count > 0)
        {
            var wallMultiMesh = new MultiMesh
            {
                Mesh = _wallMesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = wallTiles.Count
            };
            for (var i = 0; i < wallTiles.Count; i++)
            {
                wallMultiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, TileToWorld(wallTiles[i], 0.4f)));
            }

            var walls = new MultiMeshInstance3D
            {
                Name = "WallTiles",
                Multimesh = wallMultiMesh,
                MaterialOverride = _wallMaterial
            };
            _wallRoot.AddChild(walls);
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
            Mesh = _entityMesh,
            MaterialOverride = state.IsLocal ? _localEntityMaterial : _remoteEntityMaterial
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

        var localState = local.Value;
        var focus = new Vector3((float)localState.Position.X, 0, (float)localState.Position.Y);
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
                ? "\n" + FormatMovementDebug(_client.MovementDebug)
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
        if (_perfPanel is not null)
        {
            _perfPanel.Visible = _perfHudVisible;
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

        AppendSectionRow();

        if (_client is not null)
        {
            var md = _client.MovementDebug;
            _perfText.Append("interp q=")
                .Append(md.QueueDepth.ToString(CultureInfo.InvariantCulture))
                .Append(" cadence=")
                .Append(md.EffectiveCadenceMs.ToString("0.#", CultureInfo.InvariantCulture))
                .Append("ms lat=")
                .Append(md.LastLatencyMs.ToString(CultureInfo.InvariantCulture))
                .Append("ms conf=")
                .Append(md.LastConfirmedTile?.ToString() ?? "-")
                .AppendLine();
        }

        SetTextIfChanged(_perfLabel, _perfText.ToString());
    }

    private void SendHeldMovement(TimeSpan now)
    {
        if (_client is null || !_client.IsLoggedIn || _chatInput?.HasFocus() == true)
        {
            return;
        }

        // Real keyboard input takes priority; an injected (debug-channel) direction fills in otherwise.
        // Either way movement goes out through the same SendMoveStep path and per-direction cadence gate.
        var keyboard = CurrentDirection();
        var injected = keyboard.HasValue ? null : CurrentInjectedDirection();
        var direction = keyboard ?? injected;
        if (!direction.HasValue)
        {
            return;
        }

        // Send at the server tick rate, NOT the step cadence: throttling sends to the 150ms step
        // cadence beat against the server's own ~150ms step cooldown (two unsynchronized clocks),
        // producing uneven step intervals so the fixed-cadence tween froze between steps. Feeding a
        // move every tick lets the server pace steps evenly on its cooldown (extra moves are dropped
        // server-side by the cooldown), matching the tween.
        var tickMs = _client.Server is { TickRate: > 0 } server ? 1000d / server.TickRate : 50d;
        var cadence = TimeSpan.FromMilliseconds(tickMs);
        if (_nextStepAt.TryGetValue(direction.Value, out var nextAt) && now.TotalSeconds < nextAt)
        {
            return;
        }

        _client.SendMoveStep(direction.Value);
        _nextStepAt[direction.Value] = (now + cadence).TotalSeconds;

        // A one-shot injected step is consumed the moment it actually fires.
        if (injected.HasValue && _injectedSingleStep)
        {
            _injectedDirection = null;
            _injectedSingleStep = false;
        }
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

    // ---- Debug control channel host (IControlHost) -------------------------------------------
    // All members below run on the main thread: the channel is polled from _Process, so every host
    // call originates inside the render loop. No locking is required.

    void IControlHost.BeginManualMove(Direction8 direction, double durationMs)
    {
        // durationMs <= 0 => a single step (one cadence-gated SendMoveStep); otherwise hold for the window.
        StopAutopilot();
        _injectedDirection = direction;
        _injectedSingleStep = durationMs <= 0;
        _injectedUntilSeconds = durationMs > 0 ? _elapsedSeconds + (durationMs / 1000d) : 0;
    }

    void IControlHost.StopMovement()
    {
        _injectedDirection = null;
        _injectedSingleStep = false;
        _injectedUntilSeconds = 0;
        StopAutopilot();
    }

    void IControlHost.SendChat(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            _client?.SendChat(trimmed);
        }
    }

    void IControlHost.TogglePerfHud()
    {
        TogglePerfHud();
    }

    void IControlHost.ToggleFullscreen()
    {
        var mode = DisplayServer.WindowGetMode();
        DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.ExclusiveFullscreen
            ? DisplayServer.WindowMode.Windowed
            : DisplayServer.WindowMode.ExclusiveFullscreen);
    }

    bool IControlHost.TryBeginAutopilot(string pattern, double durationMs, out string error)
    {
        var legs = ResolveAutopilotPattern(pattern);
        if (legs is null)
        {
            error = $"unknown pattern '{pattern}' (square|line|zigzag|circle)";
            return false;
        }

        var duration = durationMs > 0 ? durationMs : 30000d;
        _autopilotPattern = legs;
        _autopilotLegIndex = 0;
        _injectedSingleStep = false;
        _autopilotEndsAtSeconds = _elapsedSeconds + (duration / 1000d);
        // Each leg lasts long enough to walk a few tiles before turning; scales with the step cadence.
        var cadenceSeconds = (_client?.Server?.EffectiveStepCadenceMs ?? 150d) / 1000d;
        _autopilotLegSeconds = _elapsedSeconds + Math.Max(cadenceSeconds * 4d, 0.5d);
        _injectedDirection = legs[0];
        _injectedUntilSeconds = _autopilotEndsAtSeconds;
        OpenFrameCsv();
        error = string.Empty;
        return true;
    }

    ControlTelemetry IControlHost.ReadTelemetry()
    {
        return new ControlTelemetry(
            Performance.GetMonitor(Performance.Monitor.TimeFps),
            _lastFrameMs,
            _maxFrameMs,
            _lastPollMs,
            _lastRenderStateMs,
            _lastEntitiesMs,
            _lastCameraMs,
            _lastOverlayMs,
            _maxPollMs,
            _maxRenderStateMs,
            _maxEntitiesMs,
            _maxCameraMs,
            _maxOverlayMs,
            _clientGc0Count,
            _clientGc1Count,
            _clientGc2Count,
            _frameHitchCount);
    }

    MovementDebugSnapshot IControlHost.ReadMovementDebug()
    {
        return _client?.MovementDebug ?? MovementDebugSnapshot.Empty;
    }

    IReadOnlyList<EntityRenderState> IControlHost.ReadEntities()
    {
        // _renderStates is refreshed each frame in SampleRenderStates; returning it directly avoids an
        // extra copy on the (rare) query path. The channel serializes synchronously before the next frame.
        return _renderStates;
    }

    ControlState IControlHost.ReadState()
    {
        return new ControlState(
            _client?.State.ToString() ?? "Disconnected",
            _client?.IsLoggedIn ?? false,
            _client?.Role.ToString() ?? ClientRole.Player.ToString(),
            _client?.Zone?.ZoneId ?? string.Empty,
            _client?.EntityCount ?? 0,
            _client?.LocalTile?.ToString() ?? string.Empty);
    }

    private Direction8? CurrentInjectedDirection()
    {
        if (!_injectedDirection.HasValue)
        {
            return null;
        }

        // A held injected move expires at its window end (autopilot keeps refreshing _injectedUntilSeconds).
        // A single-step move has no window and is cleared by SendHeldMovement once it fires.
        if (!_injectedSingleStep && _elapsedSeconds > _injectedUntilSeconds)
        {
            _injectedDirection = null;
            return null;
        }

        return _injectedDirection;
    }

    private void AdvanceAutopilot(TimeSpan now)
    {
        if (_autopilotPattern is null)
        {
            return;
        }

        if (_elapsedSeconds >= _autopilotEndsAtSeconds)
        {
            StopAutopilot();
            return;
        }

        if (_elapsedSeconds >= _autopilotLegSeconds)
        {
            _autopilotLegIndex = (_autopilotLegIndex + 1) % _autopilotPattern.Length;
            _injectedDirection = _autopilotPattern[_autopilotLegIndex];
            var cadenceSeconds = (_client?.Server?.EffectiveStepCadenceMs ?? 150d) / 1000d;
            _autopilotLegSeconds = _elapsedSeconds + Math.Max(cadenceSeconds * 4d, 0.5d);
        }

        _injectedUntilSeconds = _autopilotEndsAtSeconds;
    }

    private void StopAutopilot()
    {
        _autopilotPattern = null;
        _autopilotEndsAtSeconds = 0;
        _autopilotLegSeconds = 0;
        _autopilotLegIndex = 0;
        if (_injectedDirection.HasValue)
        {
            _injectedDirection = null;
            _injectedUntilSeconds = 0;
        }

        CloseFrameCsv();
    }

    private static Direction8[]? ResolveAutopilotPattern(string pattern)
    {
        return pattern.Trim().ToLowerInvariant() switch
        {
            "square" => [Direction8.N, Direction8.E, Direction8.S, Direction8.W],
            "line" => [Direction8.E, Direction8.W],
            "zigzag" => [Direction8.NE, Direction8.SE],
            "circle" => [Direction8.N, Direction8.NE, Direction8.E, Direction8.SE, Direction8.S, Direction8.SW, Direction8.W, Direction8.NW],
            _ => null
        };
    }

    private void OpenFrameCsv()
    {
        CloseFrameCsv();
        try
        {
            // res:// is src/Mmo.Client.Godot; the gitignored .run/ lives at the repo root (two levels up).
            // GlobalizePath gives an absolute path independent of the process working directory.
            var runDir = ProjectSettings.GlobalizePath("res://../../.run");
            Directory.CreateDirectory(runDir);
            var safeName = string.Concat((PlayerName ?? "client").Split(Path.GetInvalidFileNameChars()));
            if (safeName.Length == 0) { safeName = "client"; }
            _frameCsv = new StreamWriter(Path.Combine(runDir, $"client-frames-{safeName}.csv"), append: false)
            {
                AutoFlush = false
            };
            _frameCsv.WriteLine("elapsedSec,frameMs,pollMs,renderStateMs,entitiesMs,cameraMs,overlayMs,gc0,gc1,gc2");
            _frameCsvGc0 = GC.CollectionCount(0);
            _frameCsvGc1 = GC.CollectionCount(1);
            _frameCsvGc2 = GC.CollectionCount(2);
        }
        catch (IOException exception)
        {
            GD.PushWarning($"Could not open .run/client-frames.csv: {exception.Message}");
            _frameCsv = null;
        }
    }

    private void AppendFrameCsvRow()
    {
        if (_frameCsv is null)
        {
            return;
        }

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var dGc0 = gc0 - _frameCsvGc0;
        var dGc1 = gc1 - _frameCsvGc1;
        var dGc2 = gc2 - _frameCsvGc2;
        _frameCsvGc0 = gc0;
        _frameCsvGc1 = gc1;
        _frameCsvGc2 = gc2;

        var row = string.Create(CultureInfo.InvariantCulture,
            $"{_elapsedSeconds:0.###},{_lastFrameMs:0.###},{_lastPollMs:0.###},{_lastRenderStateMs:0.###},{_lastEntitiesMs:0.###},{_lastCameraMs:0.###},{_lastOverlayMs:0.###},{dGc0},{dGc1},{dGc2}");
        try
        {
            _frameCsv.WriteLine(row);
        }
        catch (IOException)
        {
            CloseFrameCsv();
        }
    }

    private void CloseFrameCsv()
    {
        if (_frameCsv is null)
        {
            return;
        }

        try
        {
            _frameCsv.Flush();
            _frameCsv.Dispose();
        }
        catch (IOException)
        {
            // Best effort.
        }

        _frameCsv = null;
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

    private static ShaderMaterial CreateGridMaterial()
    {
        // Procedural grid drawn in the fragment shader on a single ground-sized PlaneMesh, replacing
        // the old per-line ImmediateMesh geometry. Lines sit at tile boundaries (world *.5) and are
        // anti-aliased via fwidth. One plane + one draw call, scales to any zone size for free.
        return new ShaderMaterial { Shader = new Shader { Code = GridShaderCode } };
    }

    private const string GridShaderCode = @"
shader_type spatial;
render_mode unshaded;

uniform vec4 line_color : source_color = vec4(0.22, 0.42, 0.48, 0.55);
uniform float tile_size = 1.0;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 uv = world_pos.xz / tile_size;
    vec2 d = abs(fract(uv) - 0.5) / fwidth(uv);
    float line = min(d.x, d.y);
    ALBEDO = line_color.rgb;
    ALPHA = (1.0 - min(line, 1.0)) * line_color.a;
}
";

    private static StandardMaterial3D Material(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.82f
        };
    }

    private static PanelContainer CreateOverlayPanel(string name, Vector2 position, Vector2 size)
    {
        var panel = new PanelContainer
        {
            Name = name,
            Position = position,
            CustomMinimumSize = size
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.06f, 0.08f, 0.62f),
            BorderColor = new Color(0.30f, 0.45f, 0.50f, 0.55f)
        };
        style.SetCornerRadiusAll(6);
        style.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static MarginContainer CreatePanelMargin(Control panel)
    {
        var margin = new MarginContainer { Name = "Margin" };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        panel.AddChild(margin);
        return margin;
    }

    private static VBoxContainer CreatePanelVBox(PanelContainer panel)
    {
        var vbox = new VBoxContainer { Name = "Rows" };
        vbox.AddThemeConstantOverride("separation", 2);
        CreatePanelMargin(panel).AddChild(vbox);
        return vbox;
    }

    private static Label CreateOverlayLabel(string name, int fontSize)
    {
        var label = new Label
        {
            Name = name,
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

    private void AppendSectionRow()
    {
        var maxSection = _maxPollMs;
        maxSection = Math.Max(maxSection, _maxRenderStateMs);
        maxSection = Math.Max(maxSection, _maxEntitiesMs);
        maxSection = Math.Max(maxSection, _maxCameraMs);
        maxSection = Math.Max(maxSection, _maxOverlayMs);

        _perfText.Append("proc poll/rs/ent/cam/ovl=")
            .Append(_lastPollMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_lastRenderStateMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_lastEntitiesMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_lastCameraMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append('/')
            .Append(_lastOverlayMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" ms (max ")
            .Append(maxSection.ToString("0.0", CultureInfo.InvariantCulture))
            .AppendLine(")");
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
