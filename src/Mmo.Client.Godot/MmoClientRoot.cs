using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Mmo.Client.Core;
using Mmo.Client.Godot.Visuals;
using Mmo.Shared.Domain;

public partial class MmoClientRoot : Node3D, IControlHost
{
	// Stage-1 refactor (S61): all entity rendering now lives in the EntityVisual hierarchy behind an
	// EntityRenderer + factory (src/Mmo.Client.Godot/Visuals/). MmoClientRoot keeps the render-state buffer
	// (which TryHarvest + the debug-control channel also read) and the world shell, and drives the renderer.
	private readonly HashSet<TileCoord> _renderedBlockedTiles = [];
	private readonly List<EntityRenderState> _renderStates = [];
	private readonly List<string> _chatRows = [];
	private readonly StringBuilder _perfText = new(768);
	private readonly BoxMesh _wallMesh = new() { Size = new Vector3(0.92f, 0.85f, 0.92f) };
	private readonly StandardMaterial3D _groundMaterial = Material(new Color(0.08f, 0.12f, 0.13f));
	private readonly StandardMaterial3D _wallMaterial = Material(new Color(0.45f, 0.50f, 0.53f));

	// Live-tunable presentation params shared with the visuals (label pixel size/height, rock/tree/plant model
	// scale). The S65 F5 visual panel mutates these; the renderer pushes label + model-scale changes onto live
	// visuals. The rock model scale stays an [Export] for inspector tweaking, mirrored into _tuning at _Ready
	// and on each F5 apply.
	[Export] public float RockModelScale { get; set; } = 4f;
	private readonly VisualTuning _tuning = new();
	private EntityRenderer? _renderer;

	private MmoClient? _client;
	private Node3D? _worldRoot;
	private Node3D? _wallRoot;
	private Node3D? _entityRoot;
	private Camera3D? _camera;
	// Mouse-wheel zoom: live orthographic size (smaller = zoomed in), clamped. Seeded from CameraSize.
	private float _cameraSize = 28f;
	// Zoom clamp bounds — fields (not consts) so the S60 admin tuning panel can widen/narrow the zoom range
	// live. The wheel-zoom clamp in _UnhandledInput reads these each scroll.
	private float _cameraSizeMin = 8f;
	private float _cameraSizeMax = 30f;
	private const float CameraZoomStep = 2.5f;
	private Label? _statusLabel;
	private PanelContainer? _metricsPanel;
	private Label? _metricsLabel;
	private Label? _chatLabel;
	private LineEdit? _chatInput;
	private PanelContainer? _perfPanel;
	private Label? _perfLabel;
	private FrameTimeGraph? _perfGraph;
	private Label? _inventoryLabel;
	private PanelContainer? _toastPanel;
	private Label? _toastLabel;

	// ---- S60 / S65 admin live-tuning panels --------------------------------------------------------
	// F4 toggles the SERVER tuning panel (move.stepCooldownMs, move.turnDelayMs, aoi.interestRadius — ride
	// AdminSetTuning to the server, which admin-gates + clamps). F5 (S65) toggles the CLIENT-LOCAL VISUAL panel
	// (camera zoom range, rock/tree/plant model scale, label pixel-size/height — applied instantly to local
	// state/nodes, no server round-trip). Both are admin-only dev tools, not shipped UI — built once, shown only
	// for an Admin session. Splitting them keeps the server knobs unmistakable from the render knobs.
	private PanelContainer? _tuningPanel;
	private bool _tuningPanelVisible;
	private bool _tuningFieldsSeeded;
	private LineEdit? _tuneStepCooldownMs;
	private LineEdit? _tuneTurnDelayMs;
	private LineEdit? _tuneInterestRadius;

	// F5 visual panel (S65).
	private PanelContainer? _visualPanel;
	private bool _visualPanelVisible;
	private bool _visualFieldsSeeded;
	private LineEdit? _tuneCameraZoomMin;
	private LineEdit? _tuneCameraZoomMax;
	private LineEdit? _tuneRockScale;
	private LineEdit? _tuneTreeScale;
	private LineEdit? _tunePlantScale;
	private LineEdit? _tuneLabelPixelSize;
	private LineEdit? _tuneLabelHeight;
	private readonly ItemRegistry _itemRegistry = ItemRegistry.Default;
	private long _renderedInventoryVersion = -1;
	private long _lastInteractResultSequence;
	private double _toastExpiresAt;
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
	// One state for the whole dev/monitoring HUD (perf panel + server-metrics panel + status-panel
	// diagnostics). Hidden by default so the launch screen is clean; F3 (and the debug-control
	// `client_toggle_perf`) flips the entire set together.
	private bool _debugOverlayVisible;

	// Debug control channel (T2). Null unless MMO_DEBUG_CONTROL_PORT is set; absent => zero behavior change.
	private DebugControlChannel? _controlChannel;

	// Injected movement: a direction held for a fixed duration, sent on the same cadence as real input.
	// _injectedSingleStep latches a one-shot step (move with durationMs<=0) that clears once it fires.
	private Direction8? _injectedDirection;
	private double _injectedUntilSeconds;
	private bool _injectedSingleStep;

	// Held-direction movement intent (protocol v15). We send a MoveIntent only when the intent changes
	// (keydown / keyup / direction change) plus a low-rate keepalive resend, instead of streaming a step
	// every tick. The server steps the entity from the held intent at its own cooldown cadence.
	// _lastSentMoving/_lastSentDirection track what the server currently believes; the server's keepalive
	// timeout (~1 s) is the safety net if the keepalive itself is dropped.
	private const double MoveIntentKeepaliveSeconds = 0.5;

	// S64 mouse-heading feel constants. Dead-zone: ~0.6 tile (between the S64 0.5–0.75 guidance) — inside it the
	// held octant is kept so the heading doesn't whip when the cursor sits on/near the player. Hysteresis: 6° of
	// octant stickiness past the boundary before switching, killing flicker between two adjacent octants.
	private const double MouseHeadingDeadZoneTiles = 0.6;
	private const double MouseHeadingHysteresisDegrees = 6.0;
	private bool _lastSentMoving;
	private Direction8 _lastSentDirection;
	private double _nextMoveIntentKeepaliveAt;

	// S56: mouse control is hold-to-walk-toward-cursor (UO), not click-a-destination. While the RIGHT mouse
	// button is held, each frame we ray the cursor to the ground plane and hold the MoveIntent heading from
	// the PREDICTED local tile toward the cursor tile (CursorHeading) — exactly the keyboard path, re-aimed
	// live. WASD takes priority while a key is down. The S53 click-a-destination DRIVE PATH is retired: the
	// ClickMoveController and TilePathfinder CLASSES are kept (a "click once to path there" mode may return
	// later) but no longer instantiated or driven here.

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
		// S56: mouse movement is now hold-to-walk-toward-cursor (UO control), polled every frame in
		// SendHeldMovement off Input.IsMouseButtonPressed — NOT an event-driven click-a-destination. So the
		// right mouse button is intentionally not consumed here; the old HandleClickToMove path is retired.
		// Mouse-wheel zoom: shrink/grow the orthographic camera around the character.
		if (@event is InputEventMouseButton { Pressed: true } wheel)
		{
			if (wheel.ButtonIndex == MouseButton.WheelUp)
			{
				_cameraSize = Mathf.Clamp(_cameraSize - CameraZoomStep, _cameraSizeMin, _cameraSizeMax);
				GetViewport().SetInputAsHandled();
				return;
			}
			if (wheel.ButtonIndex == MouseButton.WheelDown)
			{
				_cameraSize = Mathf.Clamp(_cameraSize + CameraZoomStep, _cameraSizeMin, _cameraSizeMax);
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		if (@event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		if (key.Keycode == Key.F3)
		{
			ToggleDebugOverlay();
			GetViewport().SetInputAsHandled();
			return;
		}

		// F4: admin-only SERVER tuning panel (S60). Ignored for non-admins (panel never shows). Not consumed
		// while typing in chat so 'F4' can't be swallowed mid-message — but F4 isn't a text key anyway.
		if (key.Keycode == Key.F4)
		{
			ToggleTuningPanel();
			GetViewport().SetInputAsHandled();
			return;
		}

		// F5: admin-only CLIENT-LOCAL VISUAL tuning panel (S65 — camera zoom + model scales + label sizes).
		if (key.Keycode == Key.F5)
		{
			ToggleVisualPanel();
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

		// E = harvest. Only when not typing in chat (otherwise 'e' would both type and harvest). Targets
		// the nearest adjacent available resource node; the server re-validates adjacency authoritatively.
		if (key.Keycode == Key.E && _chatInput?.HasFocus() != true)
		{
			TryHarvest();
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

	private void TryHarvest()
	{
		if (_client?.IsLoggedIn != true || _client.LocalTile is not TileCoord actorTile)
		{
			return;
		}

		// _renderStates is refreshed every frame in SampleRenderStates; it is the same data the renderer
		// sees, so nearest-node selection matches what the player is looking at.
		if (HarvestTargeting.TryFindNearestHarvestable(_renderStates, actorTile, out var targetNetworkId))
		{
			_client.SendInteractRequest(targetNetworkId);
		}
		else
		{
			// No adjacent node: give immediate local feedback rather than a silent no-op. This is the one
			// place the client "knows" without the server, and it never mutates state — purely a hint.
			ShowInteractFeedback("No resource node in reach.");
		}
	}

	// S64 hold-to-walk-toward-cursor (UO control). Returns the heading to hold while the RIGHT mouse button is
	// down. The heading is derived from a CONTINUOUS world vector — the local player's smooth rendered position
	// (the predictor's tweened sample, what the avatar is drawn at) toward the continuous cursor ground-plane
	// hit point — NOT from the integer predicted tile and NOT from a tile-rounded cursor. CursorHeading applies
	// a dead-zone (cursor on/near the player -> no heading, so the avatar stops instead of whipping) and octant
	// hysteresis (don't flicker between adjacent octants on the boundary). Returns null when the button is up,
	// before login, when there is no local render position yet, when the ground ray misses, OR when the cursor
	// is inside the dead-zone (caller emits moving:false). Called every frame from SendHeldMovement, below WASD
	// in priority. Holding the button also clears any debug-injected/autopilot motion so the two never fight.
	private Direction8? CurrentMouseHeading()
	{
		if (!Input.IsMouseButtonPressed(MouseButton.Right))
		{
			return null;
		}

		if (_client?.IsLoggedIn != true)
		{
			return null;
		}

		// The local player's CONTINUOUS rendered world position — the same predictor-tweened sample the avatar
		// is drawn at and the camera follows (UpdateCamera reads it identically). NOT the integer predicted tile,
		// so the heading origin no longer jumps a tile per step or shifts on reconcile.
		if (!TryGetLocalRenderPosition(out var playerX, out var playerZ))
		{
			return null;
		}

		var screenPosition = GetViewport().GetMousePosition();
		if (!TryPickGroundPoint(screenPosition, out var hit))
		{
			return null;
		}

		// A deliberate mouse move overrides any debug-injected/autopilot motion so they never fight over the
		// held intent (mirrors how WASD used to pre-empt the old click-move).
		if (_injectedDirection.HasValue || _autopilotPattern is not null)
		{
			_injectedDirection = null;
			_injectedSingleStep = false;
			StopAutopilot();
		}

		// World axes: +X = east, +Z = south (the tile grid's screen-down maps to world Z). The dead-zone holds
		// the previous heading when the cursor sits on/near the player; hysteresis stops boundary flicker. The
		// "last heading" we feed is what we are currently holding (_lastSentDirection while moving) so the
		// stickiness is measured against the octant actually in effect.
		var dx = hit.X - playerX;
		var dy = hit.Z - playerZ;
		return CursorHeading.FromWorldVector(
			dx,
			dy,
			_lastSentMoving ? _lastSentDirection : (Direction8?)null,
			MouseHeadingDeadZoneTiles,
			MouseHeadingHysteresisDegrees);
	}

	// The local player's continuous rendered world position (X = east, Z = south) from the per-frame render
	// states — for the local entity this is the predictor's smooth tween (what the avatar is drawn at), exactly
	// the position UpdateCamera focuses on. Returns false before the local entity has a render state this frame.
	private bool TryGetLocalRenderPosition(out float x, out float z)
	{
		x = 0f;
		z = 0f;
		if (_client?.LocalNetworkId is not uint localNetworkId)
		{
			return false;
		}

		foreach (var state in _renderStates)
		{
			if (state.NetworkId == localNetworkId)
			{
				x = (float)state.Position.X;
				z = (float)state.Position.Y;
				return true;
			}
		}

		return false;
	}

	// Projects the mouse position onto the y=0 ground plane (the tile plane) via the camera ray, returning the
	// CONTINUOUS hit point (NOT rounded to a tile) so S64 can build a smooth player->cursor heading vector. The
	// orthographic camera looks down from a fixed offset, so the ray/plane intersection is exact. Returns false
	// only if the ray is parallel to / pointing away from the ground. NOTE FOR VISUAL CHECK: pick accuracy
	// depends on this projection — verify the avatar walks toward the world point under the cursor.
	private bool TryPickGroundPoint(Vector2 screenPosition, out Vector3 hit)
	{
		hit = default;
		if (_camera is null)
		{
			return false;
		}

		var origin = _camera.ProjectRayOrigin(screenPosition);
		var direction = _camera.ProjectRayNormal(screenPosition);
		if (Mathf.IsZeroApprox(direction.Y))
		{
			return false;
		}

		var distance = -origin.Y / direction.Y;
		if (distance < 0f)
		{
			return false;
		}

		hit = origin + (direction * distance);
		return true;
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

		// Seed the shared tuning from the exported rock scale, then stand up the entity renderer over the
		// entity root. All per-frame entity spawn/update/despawn flows through it (S61).
		_tuning.RockModelScale = RockModelScale;
		_renderer = new EntityRenderer(_entityRoot, _tuning);

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
		_cameraSize = CameraSize;
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
		_metricsPanel = metricsPanel;

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

		// Inventory HUD: top-right, below the metrics panel. Driven by the owner-only InventoryUpdate.
		var inventoryPanel = CreateOverlayPanel("InventoryPanel", Vector2.Zero, new Vector2(260, 150));
		inventoryPanel.AnchorLeft = 1f;
		inventoryPanel.AnchorRight = 1f;
		inventoryPanel.OffsetLeft = -272f;
		inventoryPanel.OffsetRight = -12f;
		inventoryPanel.OffsetTop = 350f;
		inventoryPanel.OffsetBottom = 500f;
		var inventoryRows = CreatePanelVBox(inventoryPanel);
		_inventoryLabel = CreateOverlayLabel("Inventory", 14);
		inventoryRows.AddChild(_inventoryLabel);

		// Interact feedback toast: bottom-center, above the chat panel. Brief, auto-hiding.
		var toastPanel = CreateOverlayPanel("ToastPanel", Vector2.Zero, new Vector2(420, 36));
		toastPanel.AnchorLeft = 0.5f;
		toastPanel.AnchorRight = 0.5f;
		toastPanel.AnchorTop = 1f;
		toastPanel.AnchorBottom = 1f;
		toastPanel.OffsetLeft = -210f;
		toastPanel.OffsetRight = 210f;
		toastPanel.OffsetTop = -270f;
		toastPanel.OffsetBottom = -234f;
		var toastRows = CreatePanelVBox(toastPanel);
		_toastLabel = CreateOverlayLabel("Toast", 16);
		_toastLabel.HorizontalAlignment = HorizontalAlignment.Center;
		toastRows.AddChild(_toastLabel);
		toastPanel.Visible = false;
		_toastPanel = toastPanel;

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

		BuildTuningPanel(layer);
		BuildVisualPanel(layer);

		// Dev/monitoring overlays start hidden — F3 (ToggleDebugOverlay) reveals them together. The
		// status panel stays visible but shows only a minimal always-on line until the overlay is on.
		_perfPanel.Visible = false;
		metricsPanel.Visible = false;

		layer.AddChild(statusPanel);
		layer.AddChild(metricsPanel);
		layer.AddChild(chatPanel);
		layer.AddChild(_perfPanel);
		layer.AddChild(inventoryPanel);
		layer.AddChild(toastPanel);
		layer.AddChild(inputPanel);
	}

	// S60/S65: the admin SERVER tuning panel (F4). Centered-left, hidden until F4 is pressed by an Admin session.
	// Each row is a label + a LineEdit; one Apply button at the bottom sends every server field at once via
	// AdminSetTuning (the server admin-gates + clamps authoritatively). Client-local VISUAL knobs moved to the
	// F5 panel (S65). Fields are seeded on first open from ServerHello / last-applied.
	private void BuildTuningPanel(CanvasLayer layer)
	{
		// Center-left, below the status panel and to the right of the perf HUD so F3 + F4 panels don't overlap.
		var panel = CreateOverlayPanel("TuningPanel", new Vector2(490, 154), new Vector2(360, 250));
		var rows = CreatePanelVBox(panel);

		var title = CreateOverlayLabel("TuningTitle", 15);
		title.Text = "ADMIN SERVER TUNING (F4)";
		rows.AddChild(title);

		var serverHeader = CreateOverlayLabel("TuningServerHeader", 12);
		serverHeader.Text = "— server (sent on Apply) —";
		rows.AddChild(serverHeader);
		_tuneStepCooldownMs = AddTuningField(rows, "move.stepCooldownMs", OnTuningApplyPressed);
		_tuneTurnDelayMs = AddTuningField(rows, "move.turnDelayMs", OnTuningApplyPressed);
		_tuneInterestRadius = AddTuningField(rows, "aoi.interestRadius", OnTuningApplyPressed);

		var apply = new Button { Name = "TuningApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnTuningApplyPressed;
		rows.AddChild(apply);

		panel.Visible = false;
		_tuningPanel = panel;
		layer.AddChild(panel);
	}

	// S65: the admin CLIENT-LOCAL VISUAL tuning panel (F5). Same row/Apply pattern as F4, but every field is
	// applied INSTANTLY client-side (no server round-trip): camera zoom range, rock/tree/plant model scale, and
	// the name-label pixel-size/height. Hidden until F5 is pressed by an Admin session; seeded on first open
	// from live local state (the consts/_tuning values currently in effect).
	private void BuildVisualPanel(CanvasLayer layer)
	{
		// To the right of the F4 panel so the two admin panels can be open side-by-side without overlapping.
		var panel = CreateOverlayPanel("VisualPanel", new Vector2(860, 154), new Vector2(360, 330));
		var rows = CreatePanelVBox(panel);

		var title = CreateOverlayLabel("VisualTitle", 15);
		title.Text = "ADMIN VISUAL TUNING (F5)";
		rows.AddChild(title);

		var localHeader = CreateOverlayLabel("VisualLocalHeader", 12);
		localHeader.Text = "— client-local (instant) —";
		rows.AddChild(localHeader);
		_tuneCameraZoomMin = AddTuningField(rows, "camera.zoomMin", OnVisualApplyPressed);
		_tuneCameraZoomMax = AddTuningField(rows, "camera.zoomMax", OnVisualApplyPressed);
		_tuneRockScale = AddTuningField(rows, "rock.modelScale", OnVisualApplyPressed);
		_tuneTreeScale = AddTuningField(rows, "tree.modelScale", OnVisualApplyPressed);
		_tunePlantScale = AddTuningField(rows, "plant.modelScale", OnVisualApplyPressed);
		_tuneLabelPixelSize = AddTuningField(rows, "label.pixelSize", OnVisualApplyPressed);
		_tuneLabelHeight = AddTuningField(rows, "label.height", OnVisualApplyPressed);

		var apply = new Button { Name = "VisualApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnVisualApplyPressed;
		rows.AddChild(apply);

		panel.Visible = false;
		_visualPanel = panel;
		layer.AddChild(panel);
	}

	// One labeled input row (label : LineEdit) inside a tuning panel. Returns the LineEdit so the caller can
	// seed/read it. Enter in any field runs `onSubmit` (the owning panel's apply-all) for quick iteration.
	private LineEdit AddTuningField(VBoxContainer parent, string label, Action onSubmit)
	{
		var row = new HBoxContainer { Name = $"Row_{label}" };
		row.AddThemeConstantOverride("separation", 8);

		var caption = CreateOverlayLabel($"Cap_{label}", 13);
		caption.Text = label;
		caption.CustomMinimumSize = new Vector2(170, 0);
		row.AddChild(caption);

		var edit = new LineEdit
		{
			Name = $"Edit_{label}",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		edit.AddThemeFontSizeOverride("font_size", 13);
		edit.TextSubmitted += _ => onSubmit();
		row.AddChild(edit);

		parent.AddChild(row);
		return edit;
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

	// S61: all entity spawn/position/facing/animation/depleted/label work now lives in the EntityVisual
	// hierarchy behind the EntityRenderer. This just hands it the per-frame render states (the same buffer
	// TryHarvest and the debug-control channel read) plus the elapsed-seconds clock the walk-hold latch uses.
	private void UpdateEntities()
	{
		_renderer?.Sync(_renderStates, _elapsedSeconds);
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
		_camera.Size = _cameraSize;
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
			if (_debugOverlayVisible)
			{
				// Full diagnostics — only while the debug/monitoring HUD is on.
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
					"WASD is screen-relative. W=up, D=right, S+D=down-right. Enter/T opens chat. F3 toggles the debug HUD." +
					movementDebug);
			}
			else
			{
				// Clean default: one minimal line — who you are, connection state, and the key hints.
				SetTextIfChanged(_statusLabel,
					$"{PlayerName}  {_client.State}\n" +
					"WASD to move. Enter/T to chat. E to harvest. F3 for the debug HUD.");
			}
		}

		if (_metricsLabel is not null)
		{
			SetTextIfChanged(_metricsLabel, FormatMetrics(_client));
		}

		if (_chatLabel is not null)
		{
			SetTextIfChanged(_chatLabel, FormatChat(_client));
		}

		UpdateInventory();
		UpdateInteractFeedback(now);
	}

	private void UpdateInventory()
	{
		if (_inventoryLabel is null || _client is null)
		{
			return;
		}

		var inventory = _client.Inventory;
		if (inventory.Version == _renderedInventoryVersion)
		{
			return;
		}

		_renderedInventoryVersion = inventory.Version;
		var rows = inventory.ToOrderedRows(_itemRegistry);
		if (rows.Count == 0)
		{
			SetTextIfChanged(_inventoryLabel, "INVENTORY\n(empty)");
			return;
		}

		var builder = new StringBuilder("INVENTORY");
		foreach (var row in rows)
		{
			builder.Append('\n').Append(row.DisplayName).Append(" x").Append(row.Quantity.ToString(CultureInfo.InvariantCulture));
		}

		SetTextIfChanged(_inventoryLabel, builder.ToString());
	}

	private void UpdateInteractFeedback(TimeSpan now)
	{
		if (_client?.LastInteractResult is InteractResultInfo result && result.Sequence != _lastInteractResultSequence)
		{
			_lastInteractResultSequence = result.Sequence;
			ShowInteractFeedback(result.Success ? "Harvested!" : DescribeInteractFailure(result.Reason));
		}

		// Auto-hide the toast once its window elapses.
		if (_toastPanel is { Visible: true } && now.TotalSeconds >= _toastExpiresAt)
		{
			_toastPanel.Visible = false;
		}
	}

	private void ShowInteractFeedback(string text)
	{
		if (_toastLabel is null || _toastPanel is null)
		{
			return;
		}

		SetTextIfChanged(_toastLabel, text);
		_toastPanel.Visible = true;
		_toastExpiresAt = _elapsedSeconds + 2.0d;
	}

	// Maps the server's machine-readable Interact reason codes to a short human-readable line.
	private static string DescribeInteractFailure(string reason)
	{
		return reason switch
		{
			"too_far" => "Too far from the node.",
			"depleted" => "Node is depleted.",
			"inventory_full" => "Inventory is full.",
			"rate_limited" => "Harvesting too fast.",
			"not_resource" => "That can't be harvested.",
			"no_target" => "No target.",
			"no_actor" => "No character.",
			"no_inventory" => "No inventory.",
			_ => string.IsNullOrEmpty(reason) ? "Harvest failed." : $"Harvest failed: {reason}"
		};
	}

	// F3 / client_toggle_perf: flips the whole dev/monitoring HUD (perf panel + server-metrics panel +
	// status-panel diagnostics) as one unit. Hidden by default for a clean launch screen.
	private void ToggleDebugOverlay()
	{
		_debugOverlayVisible = !_debugOverlayVisible;
		if (_perfPanel is not null)
		{
			_perfPanel.Visible = _debugOverlayVisible;
		}

		if (_metricsPanel is not null)
		{
			_metricsPanel.Visible = _debugOverlayVisible;
		}

		// Force the next overlay pass to repaint the perf HUD and the status panel immediately so the
		// toggle feels instant instead of waiting up to ~0.1s for the next throttle window.
		_nextPerfHudAt = 0;
		_nextOverlayAt = 0;
	}

	// F4: toggle the admin SERVER tuning panel (S60). Admin-only — for a non-admin (or pre-login) session the
	// panel never shows. Fields are seeded with current known values on first open of an authenticated admin
	// session so the human sees real starting points (server params reflect ServerHello).
	private void ToggleTuningPanel()
	{
		if (_tuningPanel is null)
		{
			return;
		}

		if (_client?.Role != ClientRole.Admin)
		{
			// Non-admin: keep it hidden and give a quiet hint rather than silently doing nothing.
			ShowInteractFeedback("Tuning panel requires Admin role.");
			return;
		}

		_tuningPanelVisible = !_tuningPanelVisible;
		if (_tuningPanelVisible && !_tuningFieldsSeeded)
		{
			SeedTuningFields();
			_tuningFieldsSeeded = true;
		}

		_tuningPanel.Visible = _tuningPanelVisible;
	}

	// F5: toggle the admin CLIENT-LOCAL VISUAL tuning panel (S65). Same admin gating as F4 for now — these are
	// dev knobs; the human can decide later whether a visual-only panel should be ungated.
	private void ToggleVisualPanel()
	{
		if (_visualPanel is null)
		{
			return;
		}

		if (_client?.Role != ClientRole.Admin)
		{
			ShowInteractFeedback("Visual panel requires Admin role.");
			return;
		}

		_visualPanelVisible = !_visualPanelVisible;
		if (_visualPanelVisible && !_visualFieldsSeeded)
		{
			SeedVisualFields();
			_visualFieldsSeeded = true;
		}

		_visualPanel.Visible = _visualPanelVisible;
	}

	// Seed the F4 server fields from ServerHello (the server's startup truth). Only called once on first open
	// (re-seeding would stomp values the human has typed but not yet applied).
	private void SeedTuningFields()
	{
		var serverStep = _client?.Server?.StepCooldownMs ?? 140;
		var serverTurnDelay = _client?.Server?.TurnDelayMs ?? 80;
		var serverRadius = _client?.Server?.InterestRadiusTiles ?? 35f;
		SetField(_tuneStepCooldownMs, serverStep);
		SetField(_tuneTurnDelayMs, serverTurnDelay);
		SetField(_tuneInterestRadius, serverRadius);
	}

	// Seed the F5 client-local visual fields from the live local state/_tuning. Only called once on first open.
	private void SeedVisualFields()
	{
		SetField(_tuneCameraZoomMin, _cameraSizeMin);
		SetField(_tuneCameraZoomMax, _cameraSizeMax);
		SetField(_tuneRockScale, _tuning.RockModelScale);
		SetField(_tuneTreeScale, _tuning.TreeModelScale);
		SetField(_tunePlantScale, _tuning.PlantModelScale);
		SetField(_tuneLabelPixelSize, _tuning.LabelPixelSize);
		SetField(_tuneLabelHeight, _tuning.PlayerLabelHeight);
	}

	private static void SetField(LineEdit? field, double value)
	{
		if (field is not null)
		{
			field.Text = value.ToString("0.######", CultureInfo.InvariantCulture);
		}
	}

	// F4 apply-all: parse every SERVER field and send it via AdminSetTuning (the server admin-gates + clamps
	// authoritatively). Invalid (unparseable) fields are skipped so a typo in one never blocks the others.
	private void OnTuningApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		// Server group — send the raw value; the server clamps to its registry bounds and applies live.
		if (TryReadField(_tuneStepCooldownMs, out var stepMs))
		{
			_client.SendAdminSetTuning("move.stepCooldownMs", stepMs);
		}

		// S63 turn delay — send to the server (move.turnDelayMs) AND apply the same value to the LOCAL
		// predictor so server and prediction stay in lockstep (a mismatch reintroduces the S56 snap). The
		// client tick-quantises it the same way the server does.
		if (TryReadField(_tuneTurnDelayMs, out var turnDelayMs))
		{
			_client.SendAdminSetTuning("move.turnDelayMs", turnDelayMs);
			_client.SetLocalTurnDelayMs(turnDelayMs);
		}

		if (TryReadField(_tuneInterestRadius, out var radius))
		{
			_client.SendAdminSetTuning("aoi.interestRadius", radius);
		}

		ShowInteractFeedback("Server tuning applied.");
	}

	// F5 apply-all (S65): parse every CLIENT-LOCAL VISUAL field and apply it INSTANTLY in place (no server
	// round-trip). Camera zoom range is clamped sane and the live _cameraSize re-clamped into it; model scales
	// and label sizes are mirrored into _tuning and pushed onto existing visuals so the change is visible
	// without a respawn. Invalid fields are skipped so a typo in one never blocks the others.
	private void OnVisualApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		if (TryReadField(_tuneCameraZoomMin, out var zoomMin))
		{
			_cameraSizeMin = Mathf.Clamp((float)zoomMin, 1f, 200f);
		}

		if (TryReadField(_tuneCameraZoomMax, out var zoomMax))
		{
			_cameraSizeMax = Mathf.Clamp((float)zoomMax, _cameraSizeMin, 400f);
		}

		_cameraSize = Mathf.Clamp(_cameraSize, _cameraSizeMin, _cameraSizeMax);

		if (TryReadField(_tuneRockScale, out var rockScale))
		{
			// Mirror onto both the shared tuning (the visuals read this) and the [Export] (inspector parity).
			_tuning.RockModelScale = Mathf.Clamp((float)rockScale, 0.1f, 50f);
			RockModelScale = _tuning.RockModelScale;
		}

		if (TryReadField(_tuneTreeScale, out var treeScale))
		{
			_tuning.TreeModelScale = Mathf.Clamp((float)treeScale, 0.1f, 50f);
		}

		if (TryReadField(_tunePlantScale, out var plantScale))
		{
			_tuning.PlantModelScale = Mathf.Clamp((float)plantScale, 0.1f, 50f);
		}

		if (TryReadField(_tuneLabelPixelSize, out var pixelSize))
		{
			_tuning.LabelPixelSize = Mathf.Clamp((float)pixelSize, 0.0001f, 0.02f);
		}

		if (TryReadField(_tuneLabelHeight, out var labelHeight))
		{
			_tuning.PlayerLabelHeight = Mathf.Clamp((float)labelHeight, 0f, 10f);
		}

		// Push the new label sizes AND model scales onto every existing visual so the change lands instantly
		// without a respawn (the renderer walks its live visuals; pooled ones re-read on next acquire).
		_renderer?.ApplyLabelTuningToExisting();
		_renderer?.ApplyModelScaleToExisting();

		ShowInteractFeedback("Visual tuning applied.");
	}

	private static bool TryReadField(LineEdit? field, out double value)
	{
		value = 0d;
		if (field is null)
		{
			return false;
		}

		return double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	private void UpdatePerfHud(TimeSpan now)
	{
		if (!_debugOverlayVisible || _perfLabel is null)
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
		if (_client is null || !_client.IsLoggedIn)
		{
			return;
		}

		// Determine the desired intent. While the chat box has focus we force "stopped" so held keys
		// don't drive the avatar while typing. Priority: real keyboard input (WASD) > mouse hold-to-move
		// heading > injected (debug-channel) direction. A held WASD key overrides the mouse heading while it
		// is down; the mouse heading re-aims live off the PREDICTED tile (what the player sees) each frame.
		var chatFocused = _chatInput?.HasFocus() == true;
		var keyboard = chatFocused ? null : CurrentDirection();

		Direction8? mouseDir = keyboard.HasValue || chatFocused ? null : CurrentMouseHeading();
		var injected = keyboard.HasValue || mouseDir.HasValue || chatFocused ? null : CurrentInjectedDirection();
		var direction = keyboard ?? mouseDir ?? injected;
		var moving = direction.HasValue;
		var resolvedDirection = direction ?? _lastSentDirection;

		// Input is state, not events: send a MoveIntent only when the intent changes, plus a keepalive
		// resend while moving (the server's ~1 s timeout stops a stuck avatar if a keepalive is dropped).
		// The server paces tile steps on its own cooldown from this held intent — no per-step send.
		var changed = moving != _lastSentMoving || (moving && resolvedDirection != _lastSentDirection);
		var keepaliveDue = moving && now.TotalSeconds >= _nextMoveIntentKeepaliveAt;
		if (changed || keepaliveDue)
		{
			_client.SendMoveIntent(moving, resolvedDirection);
			_lastSentMoving = moving;
			_lastSentDirection = resolvedDirection;
			_nextMoveIntentKeepaliveAt = now.TotalSeconds + MoveIntentKeepaliveSeconds;
		}

		// A one-shot injected step: with held intent the "single step" is a brief moving intent that we
		// clear right after it goes out, so the server takes exactly one cooldown step before stopping.
		if (moving && injected.HasValue && _injectedSingleStep)
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
		// durationMs <= 0 => a single step (a brief moving intent cleared after it fires); otherwise hold
		// the intent for the window.
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
		// The wire-facing name stays TogglePerfHud (debug-control protocol / client_toggle_perf), but it
		// now flips the whole unified debug overlay, matching F3.
		ToggleDebugOverlay();
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
