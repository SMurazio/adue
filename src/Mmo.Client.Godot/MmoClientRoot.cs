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
	// S95: camera focus blend + temporal smoothing (S102: now live F6 levers). Defaults reproduce TODAY's camera
	// exactly: blend 1.0 = follow the cosmetic character, smoothing 0 = hard-follow (no glide). The tracker
	// blends the confirmed tile and cosmetic position and frame-rate-independently smooths a persistent focus
	// toward it, snapping on the first frame and on teleports (> _cameraTeleportSnapTiles).
	private float _cameraFollowBlend = 1.0f;
	private float _cameraSmoothing = 15f;
	// S95 default 4 tiles. S102: now a live F6 field (was a const) feeding CameraFocusTracker.Advance's
	// teleport-snap threshold — beyond this jump the camera hard-snaps (respawn/zone change) instead of gliding.
	private float _cameraTeleportSnapTiles = 4f;
	private CameraFocusTracker _cameraFocus;
	private double _lastFrameDelta;
	private CheckBox? _uncapFpsCheck;
	private bool _fpsUncapped;
	private CheckBox? _frameCsvCheck;
	private CheckBox? _debugFacingBoxCheck;
	private CheckBox? _catoSpriteCheck;
	private CheckBox? _predictionTilesCheck;
	// S79: two flat ground markers for the predicted (green) vs confirmed/server (magenta) local tile, parented
	// under _worldRoot and repositioned each _Process frame while the F5 "Prediction tiles" toggle is on; hidden
	// (and not repositioned) when off so the default path has zero render cost. Created lazily on first toggle-on.
	private MeshInstance3D? _predictedTileMarker;
	private MeshInstance3D? _confirmedTileMarker;
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
	// F4 toggles the SERVER tuning panel (aoi.interestRadius — rides
	// AdminSetTuning to the server, which admin-gates + clamps). F5 (S65) toggles the CLIENT-LOCAL VISUAL panel
	// (camera zoom range, rock/tree/plant model scale, label pixel-size/height — applied instantly to local
	// state/nodes, no server round-trip). Both are admin-only dev tools, not shipped UI — built once, shown only
	// for an Admin session. Splitting them keeps the server knobs unmistakable from the render knobs.
	private PanelContainer? _tuningPanel;
	private bool _tuningPanelVisible;
	private bool _tuningFieldsSeeded;
	// SPEED1: the move.stepCooldownMs field was removed — the base step cooldown is now a pinned constant
	// (150 ms), not a live knob. aoi.interestRadius is the only remaining F4 server field.
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
	// S99: live Cato sprite placement — world pixel size (scale), Y offset (lift onto the tile), X offset
	// (horizontal nudge). S101 adds depth (toward-camera). Pushed onto active Cato visuals on Apply without a
	// respawn. Defaults on VisualTuning.
	private LineEdit? _tuneCatoPixelSize;
	private LineEdit? _tuneCatoYOffset;
	private LineEdit? _tuneCatoXOffset;
	private LineEdit? _tuneCatoDepth;

	// ---- S102 F6 movement / feel panel ------------------------------------------------------------
	// A dedicated admin-gated panel (F6) for the movement/camera-FEEL levers, moved off the F5 visual panel so the
	// render knobs and the feel knobs are unmistakable. All live (no restart); seeded from the current values on
	// open. Per-entity SPEED is the F6 "Move speed" dropdown (sends /speed); the GLOBAL base cooldown is a pinned
	// constant (SPEED1) — there is no longer a global move-speed server knob.
	private PanelContainer? _movementPanel;
	private bool _movementPanelVisible;
	private bool _movementFieldsSeeded;
	// Moved from F5: net latency (S93), cosmetic lead distance (S94), camera follow blend + smoothing (S95).
	private LineEdit? _moveNetLatencyMs;
	private LineEdit? _moveCosmeticLeadTiles;
	private LineEdit? _moveCameraFollowBlend;
	private LineEdit? _moveCameraSmoothing;
	// New (S102): camera teleport-snap distance (tiles) — exposes the former CameraTeleportSnapTiles const live.
	private LineEdit? _moveCameraTeleportSnapTiles;
	// RENDER1: 2-way render-mode selector (CosmeticLead / UoClientDriven) — a cycling button that calls
	// MmoClient.SetMovementRenderMode.
	private Button? _renderModeButton;
	// S106: the "Move speed" dropdown — discrete tick-quantized speeds (unnamed, numeric labels). ALWAYS shown
	// (speed is mode-agnostic). Each item carries its multiplier; selecting one sends /speed <mult> live. Populated
	// once on first open from ServerHello (base cadence + tick rate). _moveSpeedOptions is the parallel option list
	// (item index -> SpeedOption) so the selection handler can read the multiplier without re-deriving it.
	private OptionButton? _moveSpeedDropdown;
	private IReadOnlyList<MovementSpeedOptions.SpeedOption> _moveSpeedOptions = Array.Empty<MovementSpeedOptions.SpeedOption>();
	// New (S102): model B's S91 snap-to-confirmed-on-release toggle (MmoClient.SetSnapOnRelease).
	private CheckBox? _snapOnReleaseCheck;
	// New (S103): model B's commit-step-on-release toggle (MmoClient.SetCommitStepOnRelease) + threshold field
	// (MmoClient.SetCommitStepThreshold, applied on Apply/Enter).
	private CheckBox? _commitStepCheck;
	private LineEdit? _moveCommitThreshold;
	// UO2: one-line caption under the render-mode button naming what the ACTIVE mode does (self-documenting).
	private Label? _renderModeCaption;
	// UO2: the rows that are MODEL-B (CosmeticLead) ONLY — cosmetic lead distance, snap-on-release, commit-step +
	// its threshold. These are documented "inert otherwise" in MmoClient, so the F6 panel HIDES them in
	// UoClientDriven. Captured as their owning row containers so visibility toggles the
	// whole label+control row (not just the input). Re-evaluated on render-mode change and on panel open.
	private readonly List<Control> _modelBOnlyRows = new();
	// UO4: the "Stop on reversal" (settle-then-go) toggle, and the rows that are PREDICTOR-mode only (UoClientDriven)
	// — the stop-on-reversal lever lives in the predictor, so the F6 panel SHOWS it only in that mode and hides it
	// in CosmeticLead (where it is inert). Re-evaluated on render-mode change and on panel open, alongside the
	// model-B-only rows.
	private CheckBox? _stopOnReversalCheck;
	private readonly List<Control> _predictorOnlyRows = new();
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

	// Injected movement: a direction held until _injectedUntilSeconds, sent on the same cadence as real input.
	// A debug-channel move with durationMs<=0 holds indefinitely (_injectedUntilSeconds = double.MaxValue)
	// until StopMovement; durationMs>0 holds for that window.
	private Direction8? _injectedDirection;
	private double _injectedUntilSeconds;

	// Held-direction movement intent. NET1 Stage 1: input now rides an UNRELIABLE, REDUNDANT MoveInput
	// channel, so we drive it at a FIXED rate (~20 Hz) while moving instead of on-change + a 0.5 s keepalive.
	// Each send mints a fresh sequence and repeats the full current state plus a window of prior inputs, so a
	// dropped packet is superseded within one interval (no head-of-line freeze-then-jump). After a STOP we keep
	// sending the Moving=false state for a short tail (MoveInputStopTailCount packets) so a dropped STOP is
	// recovered by redundancy. A direction change still sends immediately (a fresh sequence next tick anyway).
	// _lastSentMoving/_lastSentDirection track the most recent intent; the server keepalive timeout (~1 s) is
	// the safety net if every redundant packet in a tail is lost.
	private const double MoveInputSendInterval = 1.0 / 20.0; // ~20 Hz
	private const int MoveInputStopTailCount = 8; // packets of Moving=false re-sent after a stop
	private double _nextMoveInputSendAt;
	private int _stopTailRemaining;

	// S64 mouse-heading feel constants. Dead-zone: ~0.6 tile (between the S64 0.5–0.75 guidance) — inside it the
	// held octant is kept so the heading doesn't whip when the cursor sits on/near the player. Hysteresis: 6° of
	// octant stickiness past the boundary before switching, killing flicker between two adjacent octants.
	private const double MouseHeadingDeadZoneTiles = 0.6;
	private const double MouseHeadingHysteresisDegrees = 6.0;
	private bool _lastSentMoving;
	private Direction8 _lastSentDirection;

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
	// S69: rows written since the last flush. The writer is AutoFlush=false (one flush/row would syscall every
	// frame); instead we flush every ~FrameCsvFlushEvery rows (≈0.25 s @60fps) so a live read of the CSV while
	// logging stays current to within a fraction of a second instead of freezing until CloseFrameCsv.
	private int _frameCsvRowsSinceFlush;
	private const int FrameCsvFlushEvery = 15;

	// S67 motion-quality instrumentation (diagnostics only; computed in SampleMotionMetrics each frame).
	// S69: a "snap" is now a RENDER teleport — the continuous render position jumping in ONE frame by far
	// more than the normal per-frame glide (a visible position jump), NOT a divergence sawtooth or
	// prediction-lead change. Max normal glide is run-diagonal ≈ 0.16 tile/frame at 60 fps; this absolute
	// single-frame-jump threshold sits well above it so smooth glide (incl. diagonals + direction changes)
	// reads snapCount ≈ 0 and only genuine teleports trip it. It is also frame-time-aware: a legitimately
	// long frame can glide further, so we also require the jump to exceed what the current speed could have
	// covered in that frame by a multiple (the "catch-up" factor) before counting it.
	private const double MotionSnapJumpTiles = 0.5;
	private const double MotionSnapCatchUpFactor = 4.0;
	private bool _hasLocalRender;
	private float _localRenderX;
	private float _localRenderY;
	private bool _hasConfirmed;
	private int _confirmedX;
	private int _confirmedY;
	private double _renderDivergence;
	private double _maxRenderDivergence;
	private int _renderSnapCount;
	private double _renderFrameDelta;
	private double _prevRenderFrameDelta;
	private double _currentSpeedTilesPerSec;
	private bool _hasPrevRenderPos;
	private float _prevRenderX;
	private float _prevRenderY;

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
			_frameCsvCheck?.SetPressedNoSignal(true);
		}
		// MMO_UNCAP_FPS: set to any value to START with vsync off / fps uncapped (perf testing — shows true
		// frame-time headroom in the F3 HUD). Off by default; toggle live anytime via the F5 visual panel
		// checkbox. Runtime-applied so project.godot isn't edited (the editor re-dirties that file).
		if (!string.IsNullOrWhiteSpace(ReadString("MMO_UNCAP_FPS", string.Empty)))
		{
			ApplyFpsUncap(true);
			_uncapFpsCheck?.SetPressedNoSignal(true);
		}
		_client = new MmoClient(new ClientConnectionOptions(Host, Port, ConnectionKey, PlayerName, PlayerName, "mmo-godot-client"));
		_client.Connect();
		GD.Print($"Godot MMO client connecting to {Host}:{Port} as {PlayerName}.");

		_controlChannel = DebugControlChannel.TryCreate(this);
	}

	public override void _Process(double delta)
	{
		_elapsedSeconds += delta;
		_lastFrameDelta = delta; // S95: stash for UpdateCamera's frame-rate-independent focus smoothing.
		var now = TimeSpan.FromSeconds(_elapsedSeconds);
		SampleFrameTiming(delta);

		// S86: feed THIS frame's held intent to the predictor BEFORE Poll ticks it forward, so prediction reflects
		// the current frame's WASD/mouse input instead of lagging it by one frame (phase-mismatched during fast
		// direction spam, adding to the visible wobble). The network send is unchanged (on-change + keepalive).
		// Injected/autopilot directions are still set below (after Poll) and read here one frame later — the same
		// as before this reorder (they were already fed after the tick); only human input gains the frame.
		SendHeldMovement(now);

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
		SendStartupChat();
		RequestMetrics(now);

		var t0 = Time.GetTicksUsec();
		SampleRenderStates(now);
		var t1 = Time.GetTicksUsec();
		UpdateEntities();
		var t2 = Time.GetTicksUsec();
		UpdateCamera();
		UpdatePredictionTileMarkers();
		var t3 = Time.GetTicksUsec();
		UpdateOverlay(now);
		var t4 = Time.GetTicksUsec();

		RecordSectionTiming(pollUsec, t1 - t0, t2 - t1, t3 - t2, t4 - t3);
		SampleMotionMetrics();
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

		// F6: admin-only CLIENT-LOCAL MOVEMENT/FEEL tuning panel (S102 — render mode, cosmetic lead, net latency,
		// camera blend/smoothing/teleport-snap, snap-on-release). Movement SPEED stays in F4 (server tuning).
		if (key.Keycode == Key.F6)
		{
			ToggleMovementPanel();
			GetViewport().SetInputAsHandled();
			return;
		}

		// DIAG1: Alt+Shift+R zeroes the local predictor's reconcile-outcome tallies (rec[M/C/S] in the F3
		// read-out) so the human can reset them just before a loss burst and read fresh counts. A live in-client
		// control (no restart); ignored while typing in chat. Measurement only. (Alt+R is reserved for RESYNC1's
		// Force Resync.)
		if (key.Keycode == Key.R && key.AltPressed && key.ShiftPressed && _chatInput?.HasFocus() != true)
		{
			_client?.ResetReconcileCounters();
			GetViewport().SetInputAsHandled();
			return;
		}

		// RESYNC1: Alt+R (NOT Shift -- Alt+Shift+R above is DIAG1's counter reset) manually forces a resync of
		// the local prediction onto the last server-confirmed position, clearing any stranded in-flight lead
		// under loss. The same reusable predictor primitive as the F6 "Force Resync" button. A live in-client
		// control (no restart); ignored while typing in chat.
		if (key.Keycode == Key.R && key.AltPressed && !key.ShiftPressed && _chatInput?.HasFocus() != true)
		{
			_client?.ForceResync();
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
		BuildMovementPanel(layer);

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
		var panel = CreateOverlayPanel("VisualPanel", new Vector2(860, 154), new Vector2(360, 360));
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
		// S102: the movement/camera-FEEL levers (net latency, cosmetic lead, camera blend/smoothing) moved to the
		// dedicated F6 panel (BuildMovementPanel). F5 keeps the pure VISUAL knobs (scales, labels, Cato, debug).
		// S99: live Cato sprite placement. Applied INSTANTLY client-side on Apply/Enter (no respawn): pushed onto
		// every active Cato visual via the renderer. Scale (px size) 2× the S96 first-guess by default; the Y/X
		// offsets centre the cat body on the tile (the frame centre sits above the cat, wand extending up-right).
		_tuneCatoPixelSize = AddTuningField(rows, "Cato scale (px size)", OnVisualApplyPressed);
		_tuneCatoYOffset = AddTuningField(rows, "Cato Y offset", OnVisualApplyPressed);
		_tuneCatoXOffset = AddTuningField(rows, "Cato X offset", OnVisualApplyPressed);
		// S101: toward-camera depth — slides Cato along the ground-projected camera direction (1,0,1)/√2,
		// positive = toward the camera. Live-applied like the other Cato fields (no respawn).
		_tuneCatoDepth = AddTuningField(rows, "Cato depth (toward cam)", OnVisualApplyPressed);

		// Live display toggle — flips on click, no Apply needed: vsync off / fps uncapped for perf testing.
		var uncapFps = new CheckBox { Name = "UncapFps", Text = "Uncap FPS (vsync off)", ButtonPressed = _fpsUncapped };
		uncapFps.AddThemeFontSizeOverride("font_size", 13);
		uncapFps.Toggled += ApplyFpsUncap;
		rows.AddChild(uncapFps);
		_uncapFpsCheck = uncapFps;

		// Live frame-CSV toggle — flips on click, no Apply needed: start/stop the per-frame .run/client-frames-<player>.csv
		// dump while running (S67 16-column motion trace). Reflects current state (checked if MMO_DEBUG_FRAME_LOG
		// auto-started it); toggling on opens a fresh file, toggling off flushes + disposes the writer.
		var frameCsv = new CheckBox { Name = "FrameCsv", Text = "Frame log (CSV)", ButtonPressed = _frameCsv is not null };
		frameCsv.AddThemeFontSizeOverride("font_size", 13);
		frameCsv.Toggled += ApplyFrameCsvDump;
		rows.AddChild(frameCsv);
		_frameCsvCheck = frameCsv;

		// S73 live debug toggle — flips on click, no Apply needed: render every Player (local + remote) as a
		// plain box + facing arrow instead of the character model, so facing + per-step movement are legible
		// while debugging movement feel. Toggling rebuilds already-spawned players so the swap is immediate.
		var debugFacingBox = new CheckBox { Name = "DebugFacingBox", Text = "Debug facing box", ButtonPressed = _tuning.DebugFacingBox };
		debugFacingBox.AddThemeFontSizeOverride("font_size", 13);
		debugFacingBox.Toggled += ApplyDebugFacingBox;
		rows.AddChild(debugFacingBox);
		_debugFacingBoxCheck = debugFacingBox;

		// S96 live toggle — flips on click, no Apply needed: render every Player (local + remote) as the "Cato"
		// AnimatedSprite3D billboard (idle/walk PNG frames, side-view directional flip) instead of the character
		// model. Toggling rebuilds already-spawned players so the swap is immediate. Falls back to the box if the
		// Cato art isn't imported yet.
		var catoSprite = new CheckBox { Name = "CatoSprite", Text = "Cato sprite (player)", ButtonPressed = _tuning.DebugCatoSprite };
		catoSprite.AddThemeFontSizeOverride("font_size", 13);
		catoSprite.Toggled += ApplyCatoSprite;
		rows.AddChild(catoSprite);
		_catoSpriteCheck = catoSprite;

		// S79 live debug toggle — flips on click, no Apply needed: paint the local player's PREDICTED tile (green)
		// and CONFIRMED/server tile (magenta) as flat ground markers, refreshed each frame. They overlap when in
		// sync and separate under lag, so the human can SEE the residual movement divergence while walking. Off
		// hides the markers (and skips repositioning them) so the default path is unchanged.
		var predictionTiles = new CheckBox { Name = "PredictionTiles", Text = "Prediction tiles", ButtonPressed = _tuning.DebugPredictionTiles };
		predictionTiles.AddThemeFontSizeOverride("font_size", 13);
		predictionTiles.Toggled += ApplyPredictionTiles;
		rows.AddChild(predictionTiles);
		_predictionTilesCheck = predictionTiles;

		// RENDER1/ICE1: the LOCAL player's render-mode control USED to live on the F6 movement panel (2-way
		// CosmeticLead / UoClientDriven button); it is now ICED — the selector isn't built and the client stays in
		// UoClientDriven. See BuildMovementPanel for the iced note. (Code retained for later un-icing.)

		var apply = new Button { Name = "VisualApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnVisualApplyPressed;
		rows.AddChild(apply);

		panel.Visible = false;
		_visualPanel = panel;
		layer.AddChild(panel);
	}

	// S102: the admin CLIENT-LOCAL MOVEMENT / FEEL tuning panel (F6). Holds the movement/camera-FEEL levers moved
	// off F5 (net latency, cosmetic lead distance, camera follow blend + smoothing) plus new ones (3-way render
	// mode, camera teleport-snap distance, snap-on-release). All applied INSTANTLY client-side (no server round-
	// trip, no restart) via the same Apply-all / live-toggle pattern as F4/F5. Per-entity move SPEED is the "Move
	// speed" dropdown here (sends /speed); the GLOBAL base cooldown is a pinned constant (SPEED1), not a knob.
	// Hidden until F6 is pressed by an Admin session; seeded on first open from the live local values.
	private void BuildMovementPanel(CanvasLayer layer)
	{
		// Below the F5 panel (same right column) so all three admin panels can be open without overlapping.
		var panel = CreateOverlayPanel("MovementPanel", new Vector2(860, 524), new Vector2(360, 320));
		var rows = CreatePanelVBox(panel);

		var title = CreateOverlayLabel("MovementTitle", 15);
		title.Text = "ADMIN MOVEMENT / FEEL (F6)";
		rows.AddChild(title);

		var note = CreateOverlayLabel("MovementSpeedNote", 12);
		note.Text = "— client-local (instant) · speed lives in F4 —";
		rows.AddChild(note);

		// ICE1 (2026-06-21): the render-mode SELECTOR is iced — UoClientDriven is the supported movement mode, and the
		// other render modes (CosmeticLead, ...) are unreachable from the UI but KEPT in the codebase for later. We do
		// NOT build the cycling button (so the client stays in its UoClientDriven default and cannot switch); the
		// non-UO code path — the MovementRenderMode enum values, MmoClient.SetMovementRenderMode, the LocalPlayerCosmetic
		// driver, the cadence plumbing, plus OnRenderModeCyclePressed / UpdateRenderModeButtonText / the model-B-only and
		// predictor-only contextual rows below — is all retained, so un-icing later is just re-exposing this control.
		// In place of the selector we show a disabled, self-documenting note so the panel reads cleanly.
		var renderModeRow = new HBoxContainer { Name = "Row_RenderMode" };
		renderModeRow.AddThemeConstantOverride("separation", 8);
		var renderModeCaption = CreateOverlayLabel("Cap_RenderMode", 13);
		renderModeCaption.Text = "Render mode";
		renderModeCaption.CustomMinimumSize = new Vector2(170, 0);
		renderModeRow.AddChild(renderModeCaption);
		var renderModeIced = CreateOverlayLabel("RenderModeIced", 13);
		renderModeIced.Text = "UO only (others iced)";
		renderModeIced.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		renderModeRow.AddChild(renderModeIced);
		rows.AddChild(renderModeRow);
		// _renderModeButton intentionally left null (no selector built) — UpdateRenderModeButtonText short-circuits on
		// null and is a no-op while iced; ApplyRenderModeContext is still driven on panel open (ToggleMovementPanel) so
		// the model-B-only rows stay hidden in the active UoClientDriven mode.

		// UO2: one-line caption naming what the ACTIVE mode does, so the panel is self-documenting. While iced it always
		// reflects UoClientDriven (the only reachable mode). Written by ApplyRenderModeContext on panel open.
		_renderModeCaption = CreateOverlayLabel("RenderModeCaption", 11);
		rows.AddChild(_renderModeCaption);

		// Drive the contextual caption + row visibility once for the active (UoClientDriven) mode. (Calls
		// ApplyRenderModeContext via the null-button short-circuit path; harmless while iced.)
		ApplyRenderModeContext(_client?.RenderMode ?? MovementRenderMode.UoClientDriven);

		// S106: the "Move speed" dropdown — a list of discrete tick-quantized speeds (UNNAMED, numbers only). ALWAYS
		// shown (speed is mode-agnostic, like net latency). Selecting one sets the LOCAL player's per-entity speed
		// live via /speed <multiplier> (the existing per-entity path), which scales off the pinned global base
		// cadence (SPEED1). The items are populated on first panel open (SeedMovementFields) from ServerHello's
		// base cadence + tick rate.
		var speedRow = new HBoxContainer { Name = "Row_MoveSpeed" };
		speedRow.AddThemeConstantOverride("separation", 8);
		var speedCaption = CreateOverlayLabel("Cap_MoveSpeed", 13);
		speedCaption.Text = "Move speed";
		speedCaption.CustomMinimumSize = new Vector2(170, 0);
		speedRow.AddChild(speedCaption);
		_moveSpeedDropdown = new OptionButton { Name = "MoveSpeedDropdown", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_moveSpeedDropdown.AddThemeFontSizeOverride("font_size", 13);
		_moveSpeedDropdown.ItemSelected += OnMoveSpeedSelected;
		speedRow.AddChild(_moveSpeedDropdown);
		rows.AddChild(speedRow);

		// Moved off F5 — applied on Apply/Enter:
		// S93: artificial one-way network latency (ms each way). 0 = off (default I/O path). Felt RTT ≈ 2× this.
		_moveNetLatencyMs = AddTuningField(rows, "Net latency (ms, each way)", OnMovementApplyPressed);
		// S94: how far model B's cosmetic lead glides ahead of the confirmed tile, in tiles. [0, 1]; 1.0 = current
		// model B, 0 ≈ no visible lead. Lower values shorten the visible lead (and the release snap).
		// UO2 model-B-only: register the owning row so it hides outside CosmeticLead.
		_moveCosmeticLeadTiles = AddTuningField(rows, "Cosmetic lead (tiles)", OnMovementApplyPressed);
		RegisterModelBOnlyRow(_moveCosmeticLeadTiles);
		// S95: camera focus blend between the confirmed tile (0) and the cosmetic character (1, default).
		_moveCameraFollowBlend = AddTuningField(rows, "Camera follow blend (0=tile,1=char)", OnMovementApplyPressed);
		// S95: camera follow smoothing as a per-second rate (frame-rate independent). 0 = off/hard-follow.
		_moveCameraSmoothing = AddTuningField(rows, "Camera smoothing (/s, 0=off)", OnMovementApplyPressed);
		// S102 new: camera teleport-snap distance (tiles). Beyond this single-frame jump the camera hard-snaps
		// (respawn / zone change) instead of gliding; below it the smoothing glides. Was the const = 4.
		_moveCameraTeleportSnapTiles = AddTuningField(rows, "Camera teleport-snap (tiles)", OnMovementApplyPressed);

		// S102 new live toggle — flips on click, no Apply needed: model B's S91 snap-to-confirmed-on-release.
		// ON (default) = current behavior (hard snap to the confirmed tile on keyup). OFF = let the release glide
		// settle over one cadence (no hard snap). Only affects model B (CosmeticLead); inert otherwise.
		var snapOnRelease = new CheckBox
		{
			Name = "SnapOnRelease",
			Text = "Snap on release (model B)",
			ButtonPressed = _client?.SnapOnRelease ?? true
		};
		snapOnRelease.AddThemeFontSizeOverride("font_size", 13);
		snapOnRelease.Toggled += ApplySnapOnRelease;
		rows.AddChild(snapOnRelease);
		_snapOnReleaseCheck = snapOnRelease;
		_modelBOnlyRows.Add(snapOnRelease); // UO2: model-B-only.

		// S103 new live toggle — flips on click, no Apply needed: commit a near-done step on release instead of
		// snapping back. ON (default) = release past the threshold finishes the step + sends a server-validated
		// commit (accept stays, reject snaps back). OFF = the S102 release behaviour (snap or soft-settle) always.
		// Only affects model B (CosmeticLead); inert otherwise.
		var commitStep = new CheckBox
		{
			Name = "CommitStepOnRelease",
			Text = "Commit step on release (model B)",
			ButtonPressed = _client?.CommitStepOnRelease ?? true
		};
		commitStep.AddThemeFontSizeOverride("font_size", 13);
		commitStep.Toggled += ApplyCommitStepOnRelease;
		rows.AddChild(commitStep);
		_commitStepCheck = commitStep;
		_modelBOnlyRows.Add(commitStep); // UO2: model-B-only.

		// S103 commit threshold (0..1) — how far the cosmetic lead must have glided onto the next tile at release for
		// a commit to fire. Default 0.7 ≈ "almost entirely on the next tile". Applied on Apply/Enter; clamped [0,1].
		// UO2 model-B-only: register the owning row so it hides outside CosmeticLead.
		_moveCommitThreshold = AddTuningField(rows, "Commit threshold (0..1)", OnMovementApplyPressed);
		RegisterModelBOnlyRow(_moveCommitThreshold);

		// UO4 new live toggle — flips on click, no Apply needed: settle-then-go on a ~180° reversal. ON = a 180°
		// flip while moving settles to a clean tile stop, then resumes the new direction (kills the left-right
		// bounce). OFF (default) = the current immediate reverse. Only the PREDICTOR mode (UoClientDriven) reads it;
		// hidden in CosmeticLead where it is inert.
		var stopOnReversal = new CheckBox
		{
			Name = "StopOnReversal",
			Text = "Stop on reversal",
			ButtonPressed = _client?.StopOnReversal ?? false
		};
		stopOnReversal.AddThemeFontSizeOverride("font_size", 13);
		stopOnReversal.Toggled += ApplyStopOnReversal;
		rows.AddChild(stopOnReversal);
		_stopOnReversalCheck = stopOnReversal;
		_predictorOnlyRows.Add(stopOnReversal); // UO4: predictor-modes only.

		// RESYNC1: manual Force Resync button. Calls MmoClient.ForceResync() -> LocalPlayerPredictor.ForceResync(),
		// which hard-snaps the local prediction (tile, step-seq, render) onto the last server-confirmed position and
		// clears any stranded in-flight lead. USER-TRIGGERED escape hatch for a loss-induced desync; the same primitive
		// as the Alt+R hotkey, and the one UO5/NET4 auto-tiers will call. Live, no restart; works in every render mode
		// (in cosmetic mode it is a harmless snap-to-server).
		var forceResync = new Button { Name = "ForceResync", Text = "Force Resync" };
		forceResync.AddThemeFontSizeOverride("font_size", 14);
		forceResync.Pressed += () => _client?.ForceResync();
		rows.AddChild(forceResync);

		var apply = new Button { Name = "MovementApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnMovementApplyPressed;
		rows.AddChild(apply);

		panel.Visible = false;
		_movementPanel = panel;
		layer.AddChild(panel);
	}

	// RENDER1: the render modes in cycle order, with the label shown on the F6 button for each. Trimmed to the two
	// keepers: CosmeticLead (smooth glide) and UoClientDriven (Ultima-Online-style: instant prediction + the server
	// FOLLOWS per-step commits — the default boot mode).
	private static readonly MovementRenderMode[] RenderModeCycle =
		[MovementRenderMode.CosmeticLead, MovementRenderMode.UoClientDriven];

	// S102 F6 render-mode button: cycle to the next render mode and apply it LIVE. SetMovementRenderMode re-anchors
	// the newly-active driver from the current render position so the avatar doesn't pop on the switch. No restart.
	// ICE1: retained for later un-icing but currently UNWIRED — the F6 selector button isn't built, so nothing calls
	// this. Re-expose it by rebuilding the render-mode button row in BuildMovementPanel and re-wiring Pressed here.
	private void OnRenderModeCyclePressed()
	{
		if (_client is null)
		{
			return;
		}

		var current = _client.RenderMode;
		var index = Array.IndexOf(RenderModeCycle, current);
		var next = RenderModeCycle[(index + 1) % RenderModeCycle.Length];
		_client.SetMovementRenderMode(next);
		UpdateRenderModeButtonText();
	}

	// S102: reflect the client's ACTIVE render mode on the F6 button (also called on seed so re-opening is correct).
	// UO2: also refresh the contextual panel (caption + model-B-only row visibility) so the controls always match
	// the active mode, both when cycling and when (re-)opening the panel.
	private void UpdateRenderModeButtonText()
	{
		if (_renderModeButton is null)
		{
			return;
		}

		var mode = _client?.RenderMode ?? MovementRenderMode.UoClientDriven;
		_renderModeButton.Text = mode switch
		{
			MovementRenderMode.CosmeticLead => "Cosmetic (smooth glide)",
			_ => "UO (client-driven)",
		};

		ApplyRenderModeContext(mode);
	}

	// UO2/RENDER1: make the F6 panel CONTEXTUAL to the active render mode. The cosmetic-lead distance,
	// snap-on-release, commit-step toggle, and commit-threshold rows are model-B (CosmeticLead) ONLY — documented
	// "inert otherwise" in MmoClient — so we HIDE them in UoClientDriven (hide, not grey, for cleanliness; the VBox
	// just reflows so there are no dead rows). The render-mode button and the shared rows (net latency, camera feel)
	// stay visible in every mode. Also writes the one-line "what this mode does" caption. UI-only.
	private void ApplyRenderModeContext(MovementRenderMode mode)
	{
		var isModelB = mode == MovementRenderMode.CosmeticLead;
		foreach (var row in _modelBOnlyRows)
		{
			row.Visible = isModelB;
		}

		// UO4/RENDER1: the stop-on-reversal lever is predictor-only (UoClientDriven). Show it there, hide it in
		// CosmeticLead where the predictor isn't driving.
		var isPredictorMode = mode is MovementRenderMode.UoClientDriven;
		foreach (var row in _predictorOnlyRows)
		{
			row.Visible = isPredictorMode;
		}

		if (_renderModeCaption is not null)
		{
			_renderModeCaption.Text = mode switch
			{
				MovementRenderMode.CosmeticLead =>
					"Cosmetic (smooth glide, no banking — best at low latency): confirmed steps drive sim; render glides ahead by the cosmetic lead.",
				_ =>
					"UO (client-driven — instant, server follows your steps): instant prediction; the server FOLLOWS your per-step commits.",
			};
		}
	}

	// S106: (re)populate the "Move speed" dropdown from ServerHello's base cadence + tick rate, and preselect the
	// default walk (1.0x). Built lazily on first panel open (ServerHello has landed by login, so the base cadence is
	// known). The item index maps 1:1 to _moveSpeedOptions so OnMoveSpeedSelected reads the multiplier directly.
	// SetItemMetadata is avoided (the parallel list is simpler + test-mirrored by MovementSpeedOptions).
	private void PopulateMoveSpeedDropdown()
	{
		if (_moveSpeedDropdown is null)
		{
			return;
		}

		var baseStepMs = _client?.Server?.StepCooldownMs ?? 150;
		var tickRate = _client?.Server?.TickRate ?? 20;
		_moveSpeedOptions = MovementSpeedOptions.Build(baseStepMs, tickRate);

		_moveSpeedDropdown.Clear();
		var selectIndex = 0;
		for (var i = 0; i < _moveSpeedOptions.Count; i++)
		{
			var option = _moveSpeedOptions[i];
			_moveSpeedDropdown.AddItem(option.Label, i);
			if (option.IsDefaultWalk)
			{
				selectIndex = i;
			}
		}

		if (_moveSpeedOptions.Count > 0)
		{
			// Preselect the default walk WITHOUT firing ItemSelected (we don't want to send a /speed on open — the
			// player is already at walk; selecting reflects the live state, it doesn't change it).
			_moveSpeedDropdown.Select(selectIndex);
		}
	}

	// S106: a speed item was picked — set the LOCAL player's per-entity speed live by sending /speed <multiplier>
	// (the existing chat-command path, like other dev commands). Admin-gated to match the rest of F6 (the server
	// also admin-gates /speed, so a non-admin send is a server-side no-op; we gate client-side too for clarity). The
	// server recomputes the effective cadence and replies with MovementSpeedChanged, which retunes BOTH local-player
	// drivers (predictor + cosmetic) via EntityState.SetStepCooldownMs — so the avatar's glide tracks the new
	// cadence in every render mode, including a mid-move switch.
	private void OnMoveSpeedSelected(long index)
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		if (index < 0 || index >= _moveSpeedOptions.Count)
		{
			return;
		}

		var option = _moveSpeedOptions[(int)index];
		_client.SendChat($"/speed {MovementSpeedOptions.FormatSpeedCommandArgument(option.Multiplier)}");
		ShowInteractFeedback($"Move speed: {option.Label}");
	}

	// UO2: register a tuning-field ROW (the HBox built by AddTuningField, i.e. the LineEdit's parent) as model-B
	// only, so ApplyRenderModeContext can hide the whole label+input row outside CosmeticLead.
	private void RegisterModelBOnlyRow(LineEdit field)
	{
		if (field.GetParent() is Control row)
		{
			_modelBOnlyRows.Add(row);
		}
	}

	// S102 F6 live toggle ("Snap on release (model B)"). Route the flag to the client (and the active cosmetic
	// driver) immediately — no restart. ON = the S91 hard snap on keyup; OFF = soft settle over one cadence.
	private void ApplySnapOnRelease(bool enabled)
	{
		_client?.SetSnapOnRelease(enabled);
	}

	// S103 F6 live toggle ("Commit step on release (model B)"). Route the flag to the client (and the active
	// cosmetic driver) immediately — no restart. ON = commit a near-done step on release; OFF = the S102 release.
	private void ApplyCommitStepOnRelease(bool enabled)
	{
		_client?.SetCommitStepOnRelease(enabled);
	}

	// UO4 F6 live toggle ("Stop on reversal"). Route the flag to the client (and the active predictor) immediately
	// — no restart. ON = settle-then-go on a ~180° reversal; OFF = the current immediate reverse.
	private void ApplyStopOnReversal(bool enabled)
	{
		_client?.SetStopOnReversal(enabled);
	}

	// Live vsync / fps toggle, shared by the F5 checkbox and MMO_UNCAP_FPS. Uncapped = vsync off + no fps cap
	// (perf testing — watch the true fps in the F3 HUD); capped = vsync on. Engine.MaxFps stays 0 either way;
	// vsync does the capping, so re-enabling it re-caps to the monitor refresh.
	private void ApplyFpsUncap(bool uncapped)
	{
		DisplayServer.WindowSetVsyncMode(uncapped
			? DisplayServer.VSyncMode.Disabled
			: DisplayServer.VSyncMode.Enabled);
		Engine.MaxFps = 0;
		_fpsUncapped = uncapped;
	}

	// Live frame-CSV toggle, shared by the F5 checkbox. On = open a fresh .run/client-frames-<player>.csv and start
	// appending per-frame rows; off = flush + dispose the writer so the partial capture is saved. OpenFrameCsv already
	// truncates (append: false) and CloseFrameCsv flushes, so each toggle-on starts a clean trace. Mirrors the
	// MMO_DEBUG_FRAME_LOG launch path; AppendFrameCsvRow no-ops while the writer is null.
	private void ApplyFrameCsvDump(bool enabled)
	{
		if (enabled)
		{
			OpenFrameCsv();
		}
		else
		{
			CloseFrameCsv();
		}
	}

	// S73 live debug toggle (F5 "Debug facing box"). Flip the shared VisualTuning flag and rebuild the
	// already-spawned player visuals so the swap (model rig <-> debug box+arrow) lands instantly — the renderer
	// releases each active player back to its pool and the next Sync re-acquires it under the new flag. Off
	// restores the normal PlayerVisual model. Resources/NPCs are untouched. Admin-gated like the rest of F5
	// (the panel only shows for an Admin session).
	private void ApplyDebugFacingBox(bool enabled)
	{
		_tuning.DebugFacingBox = enabled;
		_renderer?.RebuildPlayerVisuals();
	}

	// S96 live toggle (F5 "Cato sprite (player)"). Flip the shared VisualTuning flag and rebuild the
	// already-spawned player visuals so the swap (model rig <-> Cato AnimatedSprite3D billboard) lands instantly —
	// the renderer releases each active player back to its pool and the next Sync re-acquires it under the new
	// flag (the factory picks CatoSprite for a player, falling back to the box if the Cato art isn't imported).
	// Off restores the normal PlayerVisual model. DebugFacingBox takes precedence when both are on.
	private void ApplyCatoSprite(bool enabled)
	{
		_tuning.DebugCatoSprite = enabled;
		_renderer?.RebuildPlayerVisuals();
	}

	// S79 live debug toggle (F5 "Prediction tiles"). Flip the shared flag and ensure the two ground markers
	// exist (created lazily on first enable). When off, hide them immediately so nothing is drawn and the
	// per-frame UpdatePredictionTileMarkers reposition is skipped; when on, the next _Process frame positions
	// and shows them. Admin-gated like the rest of F5 (the panel only shows for an Admin session).
	private void ApplyPredictionTiles(bool enabled)
	{
		_tuning.DebugPredictionTiles = enabled;
		if (enabled)
		{
			EnsurePredictionTileMarkers();
		}
		else if (_predictedTileMarker is not null)
		{
			_predictedTileMarker.Visible = false;
			if (_confirmedTileMarker is not null)
			{
				_confirmedTileMarker.Visible = false;
			}
		}
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

	// S67: per-frame motion quality for the local player. Runs after SampleRenderStates so _renderStates
	// holds THIS frame's continuous (predictor-tweened) position. Diagnostics only — a handful of
	// subtractions, no allocation, no movement-behavior effect. Feeds the CSV row, F3 HUD, and telemetry.
	private void SampleMotionMetrics()
	{
		_hasLocalRender = TryGetLocalRenderPosition(out _localRenderX, out _localRenderY);

		if (_client?.LocalTile is TileCoord confirmed)
		{
			_hasConfirmed = true;
			_confirmedX = confirmed.X;
			_confirmedY = confirmed.Y;
		}
		else
		{
			_hasConfirmed = false;
		}

		// Render <-> confirmed divergence (tiles). Only meaningful when both are known this frame.
		if (_hasLocalRender && _hasConfirmed)
		{
			var ddx = _localRenderX - _confirmedX;
			var ddy = _localRenderY - _confirmedY;
			_renderDivergence = Math.Sqrt((ddx * ddx) + (ddy * ddy));
			_maxRenderDivergence = Math.Max(_maxRenderDivergence, _renderDivergence);
		}
		else
		{
			_renderDivergence = 0d;
		}

		// Per-frame motion delta: how far the continuous render position moved since last frame (~speed).
		if (_hasLocalRender && _hasPrevRenderPos)
		{
			var mdx = _localRenderX - _prevRenderX;
			var mdy = _localRenderY - _prevRenderY;
			_renderFrameDelta = Math.Sqrt((mdx * mdx) + (mdy * mdy));
		}
		else
		{
			_renderFrameDelta = 0d;
		}

		// Instantaneous speed (tiles/s) from the per-frame delta and the frame duration.
		_currentSpeedTilesPerSec = _lastFrameMs > 0d ? _renderFrameDelta / (_lastFrameMs / 1000d) : 0d;

		// S69 render snap = a visible teleport: the render position jumped THIS frame by far more than a
		// normal glide. Frame-time-aware so a legitimately long frame isn't miscounted: the jump must clear
		// both the absolute floor (well above run-diagonal ≈ 0.16 tile/frame @60fps) AND a multiple of the
		// previous frame's glide (a sudden catch-up vs. the prior steady-state step). Comparing against the
		// PREVIOUS frame's delta — not this frame's own (which would be self-referential) — is what makes a
		// reconcile teleport stand out against an otherwise smooth glide. Needs a real previous render
		// position so the very first sample after (re)spawn can't register a false jump.
		if (_hasLocalRender && _hasPrevRenderPos && _renderFrameDelta > MotionSnapJumpTiles
			&& _renderFrameDelta > _prevRenderFrameDelta * MotionSnapCatchUpFactor)
		{
			_renderSnapCount++;
		}

		_prevRenderFrameDelta = _renderFrameDelta;
		_prevRenderX = _localRenderX;
		_prevRenderY = _localRenderY;
		_hasPrevRenderPos = _hasLocalRender;
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
		// S95: focus on a tunable blend of the confirmed tile and the cosmetic render position, temporally
		// smoothed (frame-rate independent). Defaults (blend 1.0, smoothing 0) reproduce the old hard-follow of
		// the cosmetic position exactly. The tracker snaps on the first frame and on teleports so the camera
		// never glides from the origin or across the map.
		var (focusX, focusY) = _cameraFocus.Advance(
			localState.AuthoritativeTile.X,
			localState.AuthoritativeTile.Y,
			localState.Position.X,
			localState.Position.Y,
			_cameraFollowBlend,
			_cameraSmoothing,
			_lastFrameDelta,
			_cameraTeleportSnapTiles);
		var focus = new Vector3((float)focusX, 0, (float)focusY);
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
				var md = _client.MovementDebug;
				// DIAG1: the recovery-chain read-out is ALWAYS shown under the F3 debug HUD (a live in-client
				// toggle, no restart, no env var) so the human can read pred/conf/lead/recv/s + reconcile
				// outcomes during a loss burst. The detailed MOVE trace line stays gated behind the (env)
				// console-trace flag.
				var movementDebug = "\n" + FormatRecoveryDiag(md);
				if (_client.DebugMovementEnabled)
				{
					movementDebug += "\n" + FormatMovementDebug(md);
				}

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

	// F6: toggle the admin CLIENT-LOCAL MOVEMENT / FEEL tuning panel (S102). Same admin gating as F4/F5.
	private void ToggleMovementPanel()
	{
		if (_movementPanel is null)
		{
			return;
		}

		if (_client?.Role != ClientRole.Admin)
		{
			ShowInteractFeedback("Movement panel requires Admin role.");
			return;
		}

		_movementPanelVisible = !_movementPanelVisible;
		if (_movementPanelVisible)
		{
			if (!_movementFieldsSeeded)
			{
				SeedMovementFields();
				_movementFieldsSeeded = true;
			}

			// UO2: re-evaluate the contextual panel (caption + model-B row visibility) on EVERY open, so it always
			// matches the live render mode even on re-opens (seeding only runs once). UI-only; no state change.
			ApplyRenderModeContext(_client?.RenderMode ?? MovementRenderMode.UoClientDriven);
		}

		_movementPanel.Visible = _movementPanelVisible;
	}

	// Seed the F4 server fields from ServerHello (the server's startup truth). Only called once on first open
	// (re-seeding would stomp values the human has typed but not yet applied).
	private void SeedTuningFields()
	{
		// SPEED1: only aoi.interestRadius remains — the base step cooldown is a pinned constant, no longer tunable.
		var serverRadius = _client?.Server?.InterestRadiusTiles ?? 35f;
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
		// S102: net latency / cosmetic lead / camera blend+smoothing moved to F6 (SeedMovementFields).
		// S99: seed from the live Cato placement so re-opening the panel shows the current values.
		SetField(_tuneCatoPixelSize, _tuning.CatoPixelSize);
		SetField(_tuneCatoYOffset, _tuning.CatoYOffset);
		SetField(_tuneCatoXOffset, _tuning.CatoXOffset);
		SetField(_tuneCatoDepth, _tuning.CatoDepth);
	}

	// S102: seed the F6 client-local movement/feel fields from the live local values. Only called once on first
	// open (re-seeding would stomp un-applied edits), mirroring SeedVisualFields. The render-mode button and the
	// snap-on-release checkbox reflect the live client state directly.
	private void SeedMovementFields()
	{
		// Moved from F5 — seed from the live values so re-opening shows the current state.
		SetField(_moveNetLatencyMs, _client?.SimulatedLatencyMs ?? 0);
		SetField(_moveCosmeticLeadTiles, _client?.CosmeticLeadTiles ?? 1.0d);
		SetField(_moveCameraFollowBlend, _cameraFollowBlend);
		SetField(_moveCameraSmoothing, _cameraSmoothing);
		// New (S102).
		SetField(_moveCameraTeleportSnapTiles, _cameraTeleportSnapTiles);
		// S106: build the "Move speed" dropdown items from ServerHello (base cadence + tick rate) and preselect the
		// default walk. Done here (first open) since ServerHello has landed by login.
		PopulateMoveSpeedDropdown();
		UpdateRenderModeButtonText();
		_snapOnReleaseCheck?.SetPressedNoSignal(_client?.SnapOnRelease ?? true);
		// S103: seed the commit-step toggle + threshold field from the live client values.
		_commitStepCheck?.SetPressedNoSignal(_client?.CommitStepOnRelease ?? true);
		SetField(_moveCommitThreshold, _client?.CommitStepThreshold ?? 0.7d);
		// UO4: seed the stop-on-reversal toggle from the live client value (default OFF).
		_stopOnReversalCheck?.SetPressedNoSignal(_client?.StopOnReversal ?? false);
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
		// SPEED1: move.stepCooldownMs was removed (the base cooldown is pinned); only interest radius remains.
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

		// S102: net latency / cosmetic lead / camera follow blend + smoothing apply moved to F6 (OnMovementApplyPressed).
		// S99: live-apply the Cato sprite placement. Scale (px size) clamped to the sane sprite range used by the
		// label fields; offsets clamped to a generous tile range. Mirrored into _tuning (CatoSpriteVisual reads it
		// on next acquire) and pushed onto active Cato visuals below so the change lands without a respawn.
		if (TryReadField(_tuneCatoPixelSize, out var catoPixelSize))
		{
			_tuning.CatoPixelSize = Mathf.Clamp((float)catoPixelSize, 0.0001f, 0.05f);
		}

		if (TryReadField(_tuneCatoYOffset, out var catoYOffset))
		{
			_tuning.CatoYOffset = Mathf.Clamp((float)catoYOffset, -10f, 10f);
		}

		if (TryReadField(_tuneCatoXOffset, out var catoXOffset))
		{
			_tuning.CatoXOffset = Mathf.Clamp((float)catoXOffset, -10f, 10f);
		}

		// S101: toward-camera depth, clamped to a small tile range.
		if (TryReadField(_tuneCatoDepth, out var catoDepth))
		{
			_tuning.CatoDepth = Mathf.Clamp((float)catoDepth, -2f, 2f);
		}

		// Push the new label sizes AND model scales onto every existing visual so the change lands instantly
		// without a respawn (the renderer walks its live visuals; pooled ones re-read on next acquire).
		_renderer?.ApplyLabelTuningToExisting();
		_renderer?.ApplyModelScaleToExisting();
		_renderer?.ApplyCatoPlacementToExisting();

		ShowInteractFeedback("Visual tuning applied.");
	}

	// S102 F6 apply-all: parse every CLIENT-LOCAL MOVEMENT/FEEL field and apply it INSTANTLY in place (no server
	// round-trip, no restart). Net latency / cosmetic lead route to the client; camera blend/smoothing/teleport-
	// snap are local _camera* fields the next UpdateCamera reads. The render-mode button and snap-on-release toggle
	// apply live on click (not here). Invalid fields are skipped so a typo in one never blocks the others.
	private void OnMovementApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		// S93: artificial one-way network latency (ms). 0 = off; clamped to a sane debug range. Routed to the
		// client (no server round-trip) — the injected delay flows through the send/receive paths (felt ≈ 2× this).
		if (TryReadField(_moveNetLatencyMs, out var netLatency))
		{
			_client.SetSimulatedLatencyMs((int)Mathf.Clamp((float)netLatency, 0f, 2000f));
		}

		// S94: cosmetic lead distance (tiles) for model B. Clamped [0, 1] (the client clamps again); routed to the
		// active cosmetic driver. Default 1.0 = current model B; lower values shorten the visible lead + release snap.
		if (TryReadField(_moveCosmeticLeadTiles, out var leadTiles))
		{
			_client.SetCosmeticLeadTiles(Mathf.Clamp((float)leadTiles, 0f, 1f));
		}

		// S95: camera focus blend [0,1] and follow smoothing [0,30 /s]. The next UpdateCamera reads the new values.
		// Defaults (1.0 / 0) reproduce today's hard-follow camera.
		if (TryReadField(_moveCameraFollowBlend, out var followBlend))
		{
			_cameraFollowBlend = Mathf.Clamp((float)followBlend, 0f, 1f);
		}

		if (TryReadField(_moveCameraSmoothing, out var cameraSmoothing))
		{
			_cameraSmoothing = Mathf.Clamp((float)cameraSmoothing, 0f, 30f);
		}

		// S102: camera teleport-snap distance (tiles). Clamped to a sane range; 0 would snap every frame (no glide),
		// so the floor keeps a small minimum. The next UpdateCamera passes it to CameraFocusTracker.Advance.
		if (TryReadField(_moveCameraTeleportSnapTiles, out var teleportSnap))
		{
			_cameraTeleportSnapTiles = Mathf.Clamp((float)teleportSnap, 0.5f, 100f);
		}

		// S103: commit threshold (0..1) for model B's commit-step-on-release. Clamped [0,1] (the client clamps
		// again); routed to the active cosmetic driver. Default 0.7.
		if (TryReadField(_moveCommitThreshold, out var commitThreshold))
		{
			_client.SetCommitStepThreshold(Mathf.Clamp((float)commitThreshold, 0f, 1f));
		}

		ShowInteractFeedback("Movement tuning applied.");
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

			// S93: show the active artificial latency only while it is injected (0 = off, line omitted so the
			// default HUD is unchanged). One-way value; felt round-trip ≈ 2× (e.g. 100 ⇒ ~200ms RTT).
			if (_client.SimulatedLatencyMs > 0)
			{
				_perfText.Append("net-sim lat=")
					.Append(_client.SimulatedLatencyMs.ToString(CultureInfo.InvariantCulture))
					.Append("ms/way (~")
					.Append((_client.SimulatedLatencyMs * 2).ToString(CultureInfo.InvariantCulture))
					.AppendLine("ms RTT)");
			}
		}

		// S67 motion line: continuous render position, instantaneous speed (tiles/s), max render<->confirmed
		// divergence, and the reconcile-snap count — sub-tile motion quality alongside the perf rows.
		_perfText.Append("motion ");
		if (_hasLocalRender)
		{
			_perfText.Append('(')
				.Append(_localRenderX.ToString("0.00", CultureInfo.InvariantCulture))
				.Append(", ")
				.Append(_localRenderY.ToString("0.00", CultureInfo.InvariantCulture))
				.Append(')');
		}
		else
		{
			_perfText.Append('-');
		}

		_perfText.Append(" spd=")
			.Append(_currentSpeedTilesPerSec.ToString("0.00", CultureInfo.InvariantCulture))
			.Append("t/s div=")
			.Append(_renderDivergence.ToString("0.00", CultureInfo.InvariantCulture))
			.Append(" max=")
			.Append(_maxRenderDivergence.ToString("0.00", CultureInfo.InvariantCulture))
			.Append(" snaps=")
			.Append(_renderSnapCount.ToString(CultureInfo.InvariantCulture))
			.AppendLine();

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

		// NET1 Stage 1: input rides an unreliable, redundant channel, so drive it at a FIXED ~20 Hz while
		// moving plus a short Moving=false tail after stop. We send when ANY of:
		//   - the intent changed (immediate edge — keydown / keyup / direction change), OR
		//   - we're moving and the ~20 Hz fixed-rate interval is due, OR
		//   - we're stopped but still owe stop-tail packets (recover a dropped STOP via redundancy).
		// A change arms the stop tail on a transition to stopped; each tail packet repeats Moving=false.
		var changed = moving != _lastSentMoving || (moving && resolvedDirection != _lastSentDirection);
		if (changed && !moving)
		{
			_stopTailRemaining = MoveInputStopTailCount;
		}

		var rateDue = moving && now.TotalSeconds >= _nextMoveInputSendAt;
		var tailDue = !moving && _stopTailRemaining > 0 && now.TotalSeconds >= _nextMoveInputSendAt;
		if (changed || rateDue || tailDue)
		{
			_client.SendMoveIntent(moving, resolvedDirection);
			_lastSentMoving = moving;
			_lastSentDirection = resolvedDirection;
			_nextMoveInputSendAt = now.TotalSeconds + MoveInputSendInterval;
			if (!moving && _stopTailRemaining > 0)
			{
				_stopTailRemaining--;
			}
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
		// durationMs <= 0 => hold the intent indefinitely (until StopMovement), matching the MCP client_move
		// contract; durationMs > 0 => hold for that window. double.MaxValue is the "indefinite" sentinel so the
		// expiry check in CurrentInjectedDirection never fires for a held move.
		StopAutopilot();
		_injectedDirection = direction;
		_injectedUntilSeconds = durationMs > 0 ? _elapsedSeconds + (durationMs / 1000d) : double.MaxValue;
	}

	void IControlHost.StopMovement()
	{
		_injectedDirection = null;
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
			_frameHitchCount,
			_maxRenderDivergence,
			_renderSnapCount,
			_currentSpeedTilesPerSec);
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
		// An indefinite hold uses the double.MaxValue sentinel, so this expiry never fires until StopMovement.
		if (_elapsedSeconds > _injectedUntilSeconds)
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

	// S69: clear the session-cumulative motion counters so each fresh capture's max-divergence + snap-count
	// reflect THAT capture, not the whole session. Called from OpenFrameCsv (i.e. on each frame-log toggle-on).
	private void ResetMotionMetrics()
	{
		_maxRenderDivergence = 0d;
		_renderSnapCount = 0;
		_renderDivergence = 0d;
		_renderFrameDelta = 0d;
		_prevRenderFrameDelta = 0d;
		_hasPrevRenderPos = false;
	}

	private void OpenFrameCsv()
	{
		CloseFrameCsv();
		ResetMotionMetrics();
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
			_frameCsv.WriteLine("elapsedSec,frameMs,pollMs,renderStateMs,entitiesMs,cameraMs,overlayMs,gc0,gc1,gc2,localRenderX,localRenderY,confirmedX,confirmedY,divergence,frameDelta");
			_frameCsv.Flush();
			_frameCsvRowsSinceFlush = 0;
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

		// S67 motion columns. Render/confirmed cells are left blank when unknown this frame (pre-spawn);
		// divergence is only written when both are known, frameDelta only when a previous render pos exists.
		var renderX = _hasLocalRender ? _localRenderX.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
		var renderY = _hasLocalRender ? _localRenderY.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
		var confX = _hasConfirmed ? _confirmedX.ToString(CultureInfo.InvariantCulture) : string.Empty;
		var confY = _hasConfirmed ? _confirmedY.ToString(CultureInfo.InvariantCulture) : string.Empty;
		var divergence = _hasLocalRender && _hasConfirmed ? _renderDivergence.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
		var frameDelta = _hasLocalRender ? _renderFrameDelta.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;

		var row = string.Create(CultureInfo.InvariantCulture,
			$"{_elapsedSeconds:0.###},{_lastFrameMs:0.###},{_lastPollMs:0.###},{_lastRenderStateMs:0.###},{_lastEntitiesMs:0.###},{_lastCameraMs:0.###},{_lastOverlayMs:0.###},{dGc0},{dGc1},{dGc2},{renderX},{renderY},{confX},{confY},{divergence},{frameDelta}");
		try
		{
			_frameCsv.WriteLine(row);
			// Live flush a few times/sec so reads while logging stay current (writer is AutoFlush=false).
			if (++_frameCsvRowsSinceFlush >= FrameCsvFlushEvery)
			{
				_frameCsv.Flush();
				_frameCsvRowsSinceFlush = 0;
			}
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

	// S79: shared flat quad for the two prediction-tile markers (one tile, slightly inset so adjacent markers
	// read as distinct). Reused by both markers — one mesh, two material overrides. Built once on first enable.
	private static readonly PlaneMesh PredictionTileMarkerMesh = new() { Size = new Vector2(0.9f, 0.9f) };
	// Predicted = green, confirmed/server = magenta. Unshaded + transparent so they sit flat on the ground and
	// stay legible over any terrain; magenta is deliberately off the terrain palette. The confirmed marker hovers
	// a hair lower than the predicted one so when they coincide the predicted (green) wins the z-fight rather than
	// flickering — the human still sees green-over-magenta as "in sync".
	private static readonly StandardMaterial3D PredictedTileMarkerMaterial = MarkerMaterial(new Color(0.20f, 0.95f, 0.25f, 0.55f));
	private static readonly StandardMaterial3D ConfirmedTileMarkerMaterial = MarkerMaterial(new Color(0.95f, 0.10f, 0.80f, 0.55f));

	private static StandardMaterial3D MarkerMaterial(Color color)
	{
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
	}

	// S79: create the two prediction-tile markers under the world root on first toggle-on. Idempotent — once
	// built it just leaves them in place. They start hidden; UpdatePredictionTileMarkers shows + positions them
	// each frame while the toggle is on. No-op until the world root exists (pre-zone).
	private void EnsurePredictionTileMarkers()
	{
		if (_worldRoot is null || _predictedTileMarker is not null)
		{
			return;
		}

		_confirmedTileMarker = new MeshInstance3D
		{
			Name = "ConfirmedTileMarker",
			Mesh = PredictionTileMarkerMesh,
			MaterialOverride = ConfirmedTileMarkerMaterial,
			Visible = false
		};
		_predictedTileMarker = new MeshInstance3D
		{
			Name = "PredictedTileMarker",
			Mesh = PredictionTileMarkerMesh,
			MaterialOverride = PredictedTileMarkerMaterial,
			Visible = false
		};
		_worldRoot.AddChild(_confirmedTileMarker);
		_worldRoot.AddChild(_predictedTileMarker);
	}

	// S79: per-frame reposition of the predicted (green) + confirmed (magenta) ground markers at the local
	// player's predicted and confirmed tiles. Called every _Process frame; cheap no-op (early return) when the
	// toggle is off — markers stay hidden and nothing is touched. When on, both markers track the two tiles
	// every frame: they overlap when in sync and separate visibly under lag. Hidden whenever the local tile is
	// unknown (pre-login / between snapshots) so a stale marker never lingers off the player.
	private void UpdatePredictionTileMarkers()
	{
		if (!_tuning.DebugPredictionTiles)
		{
			return;
		}

		EnsurePredictionTileMarkers();
		if (_predictedTileMarker is null || _confirmedTileMarker is null)
		{
			return;
		}

		var confirmed = _client?.LocalTile;
		var predicted = _client?.LocalPredictedTile;
		if (confirmed is not TileCoord confirmedTile || predicted is not TileCoord predictedTile)
		{
			_predictedTileMarker.Visible = false;
			_confirmedTileMarker.Visible = false;
			return;
		}

		// Just above the ground (ground top sits at y=0; the grid plane at 0.02) so the markers read clearly
		// over terrain. Predicted sits a touch higher than confirmed so green wins the overlap z-fight.
		_confirmedTileMarker.Position = TileToWorld(confirmedTile, 0.04f);
		_predictedTileMarker.Position = TileToWorld(predictedTile, 0.05f);
		_confirmedTileMarker.Visible = true;
		_predictedTileMarker.Visible = true;
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

	// DIAG1: the 3-link recovery-chain read-out for the LOCAL player, refreshed every overlay tick (~10 Hz).
	//   pred  = the predictor's accepted-step count (the snappy local head).
	//   conf  = the last RecipientStepSeq the client has LEARNED the server accepted.
	//   lead  = pred - conf, the in-flight steps that must DRAIN for the prediction to recover. A lead that never
	//           returns toward 0 under loss is the permanent strand DIAG1 is hunting.
	//   recv/s= snapshots applied per second (is the server->client confirm channel alive?).
	//   rec   = reconcile outcomes since the last reset (Matched / Corrected / Snapped). Mostly-Matched = the lead
	//           drained via benign confirms; climbing Corrected/Snapped = the server's confirm is diverging from
	//           the prediction (forced re-base). Reset with Alt+Shift+R (ResetReconcileCounters).
	// Pair this with the server-side trace (srvSeq / recvCommits / rejects, ServerMovementTrace mmo_trace
	// event=commit_counters) to localise the stuck link per docs/movement-loss-degradation-tiers.md.
	private static string FormatRecoveryDiag(MovementDebugSnapshot d)
	{
		return $"DIAG pred={d.PredictedStepSeq} conf={d.ConfirmedStepSeq} lead={d.LeadSteps} " +
			$"recv/s={d.SnapshotsPerSecond:0.0} " +
			$"rec[M/C/S]={d.ReconcileMatched}/{d.ReconcileCorrected}/{d.ReconcileSnapped}";
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
