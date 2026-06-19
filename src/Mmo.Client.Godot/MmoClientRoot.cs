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
	// Entity visuals are heterogeneous now: resources stay a MeshInstance3D box; players are a Node3D
	// wrapper holding the instanced character model (see PlayerModelVisual). The dict holds the common
	// Node3D base so both lifecycles (spawn/position/despawn) share one path.
	private readonly Dictionary<uint, Node3D> _entityNodes = [];
	private readonly Dictionary<uint, Label3D> _entityLabels = [];
	// Per-player rig state: the model child node (rotated for facing), its AnimationPlayer + resolved
	// walk clip, and the movement tracker that drives walk-vs-idle. Keyed by entity NetworkId; entries
	// are removed alongside _entityNodes on despawn.
	private readonly Dictionary<uint, PlayerModelVisual> _playerVisuals = [];
	private readonly HashSet<TileCoord> _renderedBlockedTiles = [];
	private readonly List<EntityRenderState> _renderStates = [];
	private readonly HashSet<uint> _seenEntityIds = [];
	private readonly List<uint> _staleEntityIds = [];
	private readonly List<string> _chatRows = [];
	private readonly StringBuilder _perfText = new(768);
	private readonly BoxMesh _wallMesh = new() { Size = new Vector3(0.92f, 0.85f, 0.92f) };
	private readonly CapsuleMesh _entityMesh = new() { Radius = 0.28f, Height = 0.9f };
	// Resource nodes use a chunky box so they read as scenery, not avatars (capsules). Distinct mesh +
	// colour so a node is unmistakable from a player at a glance.
	private readonly BoxMesh _resourceMesh = new() { Size = new Vector3(0.7f, 0.7f, 0.7f) };
	private readonly StandardMaterial3D _groundMaterial = Material(new Color(0.08f, 0.12f, 0.13f));
	private readonly StandardMaterial3D _wallMaterial = Material(new Color(0.45f, 0.50f, 0.53f));
	private readonly StandardMaterial3D _localEntityMaterial = Material(new Color(0.22f, 0.70f, 1.0f));
	private readonly StandardMaterial3D _remoteEntityMaterial = Material(new Color(0.94f, 0.68f, 0.22f));
	// Available = lush green; depleted = dim grey (and the node is also hidden when depleted, but the
	// material keeps it readable if a future build chooses to show stumps instead of hiding them).
	private readonly StandardMaterial3D _resourceAvailableMaterial = Material(new Color(0.32f, 0.78f, 0.30f));
	private readonly StandardMaterial3D _resourceDepletedMaterial = Material(new Color(0.28f, 0.30f, 0.28f));

	// ---- S54 player character model ---------------------------------------------------------------
	// The rigged humanoid that replaces the player capsule. Loaded once, instanced per Player entity.
	private const string PlayerModelPath = "res://content/characters/ProvaPersonaggioWalkLoop.glb";

	// TUNABLE (human eyeballs on relaunch). Character-Creator / Tripo rigs commonly import at real-world
	// metres (~1.7 units tall); the grid is 1 unit = 1 tile, so the raw model is too big. 0.5 is a first
	// guess aiming for ~roughly-one-tile-tall. If the model is huge -> shrink; tiny -> grow.
	private const float PlayerModelScale = 0.5f;

	// TUNABLE. glTF/Godot forward is -Z; a Direction8 of N maps to -Z in our world (tile delta (0,-1)).
	// If the model's mesh faces +Z (away from the look direction) or sideways, correct it here in degrees
	// (e.g. 180 if it faces backwards, +/-90 if sideways). 0 = trust the model's authored forward.
	private const float ModelForwardOffsetDegrees = 0f;

	// Vertical offset for the model root so the feet sit on the ground plane (y=0). Most rigs are authored
	// with the origin at the feet; if it floats or sinks, nudge here. TUNABLE.
	private const float PlayerModelYOffset = 0f;

	// Keep the walk animation playing for this long after the last detected positional change, so the
	// brief idle gap between server-confirmed tile steps does not stutter the walk loop on/off. TUNABLE.
	private const double PlayerWalkHoldSeconds = 0.2d;

	// A tile step is ~1 unit; treat per-frame displacement above this (squared) as "moving". Small enough
	// to catch slow interpolation, large enough to ignore float jitter at rest.
	private const double PlayerMovingEpsilonSquared = 0.0000004d; // ~(0.0006 unit/frame)

	// Loaded lazily on first player spawn so a build with no players never pays the load. Null + a logged
	// warning if the resource is missing/unloadable; players then fall back to the capsule mesh.
	private PackedScene? _playerModelScene;
	private bool _playerModelLoadAttempted;
	private bool _playerModelLoadFailed;

	private MmoClient? _client;
	private Node3D? _worldRoot;
	private Node3D? _wallRoot;
	private Node3D? _entityRoot;
	private Camera3D? _camera;
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
	private bool _lastSentMoving;
	private Direction8 _lastSentDirection;
	private double _nextMoveIntentKeepaliveAt;

	// Click-to-move (S52): A* over the locally-regenerated blocked map (built once the zone is known)
	// feeds the SAME held-direction MoveIntent WASD uses. The driver advances on server-confirmed tiles
	// (no prediction); any WASD input or a new right-click cancels/replaces the active path.
	private TilePathfinder? _pathfinder;
	private readonly PathDriver _pathDriver = new();

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
			_pathfinder = TilePathfinder.FromZone(_client.Zone);
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
		// Right-click to move: pick the clicked ground tile and path to it. _UnhandledInput only fires
		// for events no UI Control consumed, so clicks over the (MouseFilter.Stop) overlay panels never
		// reach here — that is the "ignore clicks over UI" guarantee, for free.
		if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouse)
		{
			HandleClickToMove(mouse.Position);
			GetViewport().SetInputAsHandled();
			return;
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

	// Right-click handler: screen -> ground tile -> A* path -> drive. Replaces any active path (a new
	// click repaths from the current tile) and cancels debug-injected movement. Unreachable / no-tile
	// gives the existing interact-feedback toast and does not move.
	private void HandleClickToMove(Vector2 screenPosition)
	{
		if (_client?.IsLoggedIn != true || _pathfinder is null || _client.LocalTile is not TileCoord start)
		{
			return;
		}

		if (!TryPickGroundTile(screenPosition, out var goal))
		{
			return;
		}

		if (!_pathfinder.IsWalkable(goal))
		{
			ShowInteractFeedback("Can't reach there.");
			return;
		}

		var path = _pathfinder.FindPath(start, goal);
		if (path.Count == 0)
		{
			// start == goal is a harmless no-op (already there); a genuinely unreachable goal gets feedback.
			if (goal != start)
			{
				ShowInteractFeedback("Can't reach there.");
			}

			return;
		}

		// A deliberate move command overrides any debug-injected/autopilot motion so the two input
		// sources never fight over the held intent.
		_injectedDirection = null;
		_injectedSingleStep = false;
		StopAutopilot();
		_pathDriver.Start(path);
	}

	// Projects the mouse position onto the y=0 ground plane (the tile plane) via the camera ray and
	// rounds to the nearest tile. The orthographic camera looks down at the world from a fixed offset, so
	// a ray/plane intersection is exact. Returns false only if the ray is parallel to / pointing away
	// from the ground; bounds/walkability of the resulting tile are checked by the caller via the
	// pathfinder. NOTE FOR VISUAL CHECK: pick accuracy depends on this projection — verify the
	// tile the avatar walks to matches the tile under the cursor across the screen.
	private bool TryPickGroundTile(Vector2 screenPosition, out TileCoord tile)
	{
		tile = default;
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

		var hit = origin + (direction * distance);
		tile = new TileCoord(Mathf.RoundToInt(hit.X), Mathf.RoundToInt(hit.Z));
		return true;
	}

	// Pumps the active click-to-move path each frame from the server-confirmed local tile, returning the
	// direction to hold (or null to stop). Returns false when no path is driving, so the caller falls back
	// to keyboard/injected input. Called from SendHeldMovement BEFORE keyboard so WASD can pre-empt.
	private bool TryGetPathDirection(out Direction8 direction)
	{
		direction = default;
		if (!_pathDriver.IsActive || _client?.LocalTile is not TileCoord confirmed)
		{
			return false;
		}

		var command = _pathDriver.Update(confirmed);
		switch (command.Action)
		{
			case PathDriveAction.Move:
				direction = command.Direction;
				return true;
			case PathDriveAction.Stop:
			default:
				// Arrived (or desynced): fall through to "no path direction" so SendHeldMovement emits
				// moving:false. The driver has already flipped IsActive off.
				return false;
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
			if (state.Kind == EntityKind.Resource && node is MeshInstance3D resourceNode)
			{
				// Drive node availability purely off the replicated Depleted bit: hide a harvested node
				// and grey it; restore (show + green) when the server respawns it. No prediction.
				resourceNode.Visible = !state.Depleted;
				resourceNode.MaterialOverride = state.Depleted ? _resourceDepletedMaterial : _resourceAvailableMaterial;
			}
			else if (_playerVisuals.TryGetValue(state.NetworkId, out var visual))
			{
				// Players: face the movement direction and play the walk loop while moving, idle otherwise.
				// "Moving" is inferred purely from the interpolated render position changing (the same
				// position that drives node.Position above) — no movement/interp logic is touched here.
				UpdatePlayerModel(visual, state);
			}

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
			// QueueFree on the wrapper frees the instanced model (and its AnimationPlayer) and the label
			// child with it; dropping the _playerVisuals entry releases our last reference to the rig.
			_entityNodes[stale].QueueFree();
			_entityNodes.Remove(stale);
			_entityLabels.Remove(stale);
			_playerVisuals.Remove(stale);
		}
	}

	private Node3D CreateEntityNode(EntityRenderState state)
	{
		// Players render as the instanced character model (with a fallback to the capsule if the model
		// could not be loaded); resources stay a flat-shaded box.
		if (state.Kind == EntityKind.Player)
		{
			var playerNode = TryCreatePlayerNode(state);
			if (playerNode is not null)
			{
				return playerNode;
			}
		}

		var isResource = state.Kind == EntityKind.Resource;
		var body = new MeshInstance3D
		{
			Name = $"Entity_{state.NetworkId}",
			Mesh = isResource ? _resourceMesh : _entityMesh,
			MaterialOverride = isResource
				? (state.Depleted ? _resourceDepletedMaterial : _resourceAvailableMaterial)
				: (state.IsLocal ? _localEntityMaterial : _remoteEntityMaterial),
			Visible = !(isResource && state.Depleted)
		};

		AttachLabel(body, state, isResource ? 0.7f : 0.9f);
		return body;
	}

	// Builds the player wrapper: a Node3D positioned by the interpolator (like the old capsule), holding
	// the instanced GLB as a child so we can scale/rotate the model without touching the wrapper's
	// interp-driven Position. Returns null if the model resource is unavailable so the caller falls back
	// to the capsule. Registers a PlayerModelVisual for per-frame facing + walk-animation driving.
	private Node3D? TryCreatePlayerNode(EntityRenderState state)
	{
		var scene = LoadPlayerModelScene();
		if (scene is null || scene.Instantiate() is not Node3D model)
		{
			return null;
		}

		var wrapper = new Node3D { Name = $"Entity_{state.NetworkId}" };
		model.Name = "Model";
		model.Scale = new Vector3(PlayerModelScale, PlayerModelScale, PlayerModelScale);
		model.Position = new Vector3(0f, PlayerModelYOffset, 0f);
		wrapper.AddChild(model);

		var animationPlayer = FindAnimationPlayer(model);
		var walkClip = ResolveWalkClip(animationPlayer);
		var visual = new PlayerModelVisual(model, animationPlayer, walkClip)
		{
			LastPosition = new Vector3((float)state.Position.X, 0f, (float)state.Position.Y)
		};
		_playerVisuals[state.NetworkId] = visual;
		ApplyFacing(visual, state.Facing);

		// Label sits a bit above the rig; the model is ~PlayerModelScale tall so keep it near the capsule
		// height for visual continuity. The human can nudge if the head clips the label.
		AttachLabel(wrapper, state, 0.9f);
		return wrapper;
	}

	private void AttachLabel(Node3D parent, EntityRenderState state, float height)
	{
		var label = new Label3D
		{
			Name = "Name",
			Text = state.DisplayName,
			PixelSize = 0.025f,
			Position = new Vector3(0, height, 0),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled
		};
		parent.AddChild(label);
		_entityLabels[state.NetworkId] = label;
	}

	// Loads (once) the player model PackedScene. Failures are logged a single time; subsequent player
	// spawns then silently fall back to the capsule rather than re-attempting/spamming the log.
	private PackedScene? LoadPlayerModelScene()
	{
		if (_playerModelLoadFailed)
		{
			return null;
		}

		if (_playerModelLoadAttempted)
		{
			return _playerModelScene;
		}

		_playerModelLoadAttempted = true;
		_playerModelScene = GD.Load<PackedScene>(PlayerModelPath);
		if (_playerModelScene is null)
		{
			_playerModelLoadFailed = true;
			GD.PushWarning($"S54: could not load player model '{PlayerModelPath}'; falling back to capsule.");
		}

		return _playerModelScene;
	}

	private static AnimationPlayer? FindAnimationPlayer(Node node)
	{
		if (node is AnimationPlayer player)
		{
			return player;
		}

		foreach (var child in node.GetChildren())
		{
			if (FindAnimationPlayer(child) is AnimationPlayer found)
			{
				return found;
			}
		}

		return null;
	}

	// Robustly pick the walk clip out of the instanced AnimationPlayer's library: prefer a name that
	// looks like the walk loop ("catwalk"/"loop"/"walk"), otherwise the first clip that is NOT the
	// T-pose, and set it to loop. Returns null (logged once) if there is no usable non-T-pose clip, in
	// which case the model simply stands still — no crash.
	private static string? ResolveWalkClip(AnimationPlayer? player)
	{
		if (player is null)
		{
			GD.PushWarning("S54: player model has no AnimationPlayer; rig will not animate.");
			return null;
		}

		var clips = player.GetAnimationList();
		string? firstNonTPose = null;
		foreach (var name in clips)
		{
			if (IsTPoseClip(name))
			{
				continue;
			}

			firstNonTPose ??= name;
			var lower = name.ToLowerInvariant();
			if (lower.Contains("catwalk") || lower.Contains("loop") || lower.Contains("walk"))
			{
				SetClipLooping(player, name);
				return name;
			}
		}

		if (firstNonTPose is not null)
		{
			SetClipLooping(player, firstNonTPose);
			return firstNonTPose;
		}

		GD.PushWarning("S54: no non-T-pose animation found on the player model; rig will not walk.");
		return null;
	}

	private static bool IsTPoseClip(string name)
	{
		var lower = name.ToLowerInvariant();
		return lower.Contains("t-pose") || lower.Contains("tpose") || lower.Contains("t_pose");
	}

	private static void SetClipLooping(AnimationPlayer player, string clipName)
	{
		// GetAnimation returns the shared Animation resource; forcing its loop mode makes Play() loop it
		// regardless of how the clip was authored/imported.
		var animation = player.GetAnimation(clipName);
		if (animation is not null)
		{
			animation.LoopMode = Animation.LoopModeEnum.Linear;
		}
	}

	// Per-frame player rig update: detect movement from the interpolated render position, play/stop the
	// walk loop accordingly (with a short hold to bridge the idle gap between confirmed tile steps), and
	// rotate the model to the entity's 8-way facing.
	private void UpdatePlayerModel(PlayerModelVisual visual, EntityRenderState state)
	{
		var position = new Vector3((float)state.Position.X, 0f, (float)state.Position.Y);
		var moved = position.DistanceSquaredTo(visual.LastPosition) > PlayerMovingEpsilonSquared;
		visual.LastPosition = position;
		if (moved)
		{
			visual.MovingUntilSeconds = _elapsedSeconds + PlayerWalkHoldSeconds;
		}

		var moving = _elapsedSeconds <= visual.MovingUntilSeconds;
		DrivePlayerAnimation(visual, moving);
		ApplyFacing(visual, state.Facing);
	}

	private void DrivePlayerAnimation(PlayerModelVisual visual, bool moving)
	{
		if (visual.AnimationPlayer is null || visual.WalkClip is null)
		{
			return;
		}

		if (moving)
		{
			if (!visual.IsWalking)
			{
				visual.AnimationPlayer.Play(visual.WalkClip);
				visual.IsWalking = true;
			}
		}
		else if (visual.IsWalking)
		{
			// Stop (snap to rest) rather than pause so an idle character stands in the rig's rest pose
			// instead of freezing mid-stride.
			visual.AnimationPlayer.Stop();
			visual.IsWalking = false;
		}
	}

	// Rotate the model so its forward axis points along the entity's 8-way Facing. Direction8 maps to a
	// tile delta; we convert that to a world heading (X=tileX, Z=tileY) and yaw the model to it, plus the
	// tunable ModelForwardOffsetDegrees correction for the rig's authored forward axis.
	private static void ApplyFacing(PlayerModelVisual visual, Direction8 facing)
	{
		var delta = facing.Delta();
		if (delta.X == 0 && delta.Y == 0)
		{
			return;
		}

		// Godot's default model forward is -Z. A yaw θ about +Y turns -Z into (-sinθ, 0, -cosθ); solving
		// that to equal the world heading (delta.X, delta.Y) gives θ = atan2(-x, -y). So N (delta 0,-1)
		// yields 0 (no rotation, already facing -Z), E (1,0) yields -90°, S (0,1) yields 180°, etc.
		var yaw = Mathf.Atan2(-delta.X, -delta.Y);
		visual.Model.Rotation = new Vector3(0f, yaw + Mathf.DegToRad(ModelForwardOffsetDegrees), 0f);
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
		// don't drive the avatar while typing. Priority: real keyboard input (WASD) > click-to-move path
		// > injected (debug-channel) direction. WASD pre-empts an active path (cancel below); the path
		// driver advances on server-confirmed tiles, not prediction.
		var chatFocused = _chatInput?.HasFocus() == true;
		var keyboard = chatFocused ? null : CurrentDirection();

		// Any manual WASD input cancels an active click-to-move path so the player regains direct control.
		if (keyboard.HasValue && _pathDriver.IsActive)
		{
			_pathDriver.Cancel();
		}

		Direction8? pathDir = keyboard.HasValue ? null : (TryGetPathDirection(out var pd) ? pd : null);
		var injected = keyboard.HasValue || pathDir.HasValue || chatFocused ? null : CurrentInjectedDirection();
		var direction = keyboard ?? pathDir ?? injected;
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

	// Mutable per-player rig state. Reference type so the dict entry is updated in place each frame.
	// Holds the model child node (rotated for facing), the AnimationPlayer + resolved walk clip (either
	// may be null when the rig lacks one — guarded everywhere), and the movement tracker driving the
	// walk/idle switch. The wrapper Node3D (interp-driven) is the dict's _entityNodes value, not stored
	// here, so freeing the wrapper on despawn frees the model and its AnimationPlayer with it.
	private sealed class PlayerModelVisual(Node3D model, AnimationPlayer? animationPlayer, string? walkClip)
	{
		public Node3D Model { get; } = model;
		public AnimationPlayer? AnimationPlayer { get; } = animationPlayer;
		public string? WalkClip { get; } = walkClip;
		public Vector3 LastPosition { get; set; }
		public double MovingUntilSeconds { get; set; }
		public bool IsWalking { get; set; }
	}
}
