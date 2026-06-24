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
	// COMBAT-QOL: floating "-N" damage numbers. Created over the entity root in BuildSceneShell, fed each frame from
	// the client's drained DamageEvents (anchored at the victim visual's live position), and advanced for rise/fade.
	private FloatingTextManager? _floatingText;
	private readonly List<DamageEvent> _damageEventScratch = new();

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
	// exactly: blend 1.0 = follow the rendered character position, smoothing 0 = hard-follow (no glide). The tracker
	// blends the confirmed tile and rendered position and frame-rate-independently smooths a persistent focus
	// toward it, snapping on the first frame and on teleports (> _cameraTeleportSnapTiles).
	private float _cameraFollowBlend = 1.0f;
	// STUTTER FIX: a LOW smoothing (user-preferred 3). The 15 it had drifted to ran an exponential focus chase
	// (focus += (target-focus)*t) that moves fast when behind / slow when close = the "accelerate to catch up" the
	// player felt but the player-render frame-log couldn't show (it logs the avatar, not the camera). 0 = hard-follow
	// the (already smooth) character; 3 adds a touch of glide without the catch-up. NOTE: at a FIXED rate the lag =
	// move-speed / rate, so faster speeds trail more — auto-scaling the rate with speed would hold the lag constant.
	// Live-tunable via the F1 Movement tab.
	private float _cameraSmoothing = 3f;
	// CAMERA-EXPERIMENT: when true the camera targets the DISCRETE predicted tile instead of the smooth character
	// render (live F1 Movement toggle). Default off = follow the character. Pairs with smoothing > 0 to glide.
	private bool _cameraTrackPredictedTile;
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
	// F1 Visual "Spawner tiles" toggle — default OFF. Debug viz of the monster spawner anchors (red tiles), gated
	// exactly like the prediction-tiles markers. Flipped by ApplySpawnerTiles; read by UpdateMonsterHomeMarkers.
	private bool _showSpawnerTiles;
	// S79: two flat ground markers for the predicted (green) vs confirmed/server (magenta) local tile, parented
	// under _worldRoot and repositioned each _Process frame while the F5 "Prediction tiles" toggle is on; hidden
	// (and not repositioned) when off so the default path has zero render cost. Created lazily on first toggle-on.
	private MeshInstance3D? _predictedTileMarker;
	private MeshInstance3D? _confirmedTileMarker;

	// LIVING-ENEMIES P3: one flat RED ground marker per known SPAWNER (the persistent leash/de-aggro anchor), keyed by
	// the stable spawner id, parented under _worldRoot. Synced each _Process frame from MmoClient.SpawnerMarkers: a
	// marker is created when a spawner enters AOI and freed when it leaves. Because it tracks the SPAWNER (not the
	// monster), the red tile stays put across the monster's death/respawn — the de-aggro anchor stays legible.
	private readonly System.Collections.Generic.Dictionary<uint, MeshInstance3D> _monsterHomeMarkers = new();
	private readonly System.Collections.Generic.List<uint> _monsterHomeStaleScratch = new();

	// FREEAIM: a flat WEDGE (pie-slice) mesh flashed on the ground from the local player, oriented along the aim,
	// showing the free-aim sector's danger area (half-angle + radius matching the server). One MeshInstance3D under
	// the world root; positioned + yawed on attack and hidden after a brief window by UpdateAimWedge.
	private MeshInstance3D? _aimWedge;
	private ulong _aimWedgeHideAtMs;
	private Label? _statusLabel;
	private PanelContainer? _metricsPanel;
	private Label? _metricsLabel;
	private Label? _chatLabel;
	private LineEdit? _chatInput;
	// Perf HUD readout + frame-time graph — a STANDALONE glanceable overlay (the old F3 perf HUD), separate from the
	// F1 tuning panel and toggled by F3 (and the client_toggle_perf control-channel command). Available to EVERYONE
	// (it was the non-admin overlay). _perfPanel hosts the readout label + the FrameTimeGraph + the uncap-fps/frame-log
	// toggles; its visibility drives _debugOverlayVisible (perf HUD readout + metrics panel + full status diag follow it).
	private Label? _perfLabel;
	private PanelContainer? _perfPanel;
	private bool _perfPanelVisible;
	private FrameTimeGraph? _perfGraph;
	private PanelContainer? _toastPanel;
	private Label? _toastLabel;

	// ---- F1 tuning panel (TabContainer) -----------------------------------------------------------
	// One DRAGGABLE tabbed panel under F1 holding the FIVE admin tuning surfaces (the old F4–F8 F-key panels) as
	// thematic TABS: Visual / Movement / Combat / Server / Vitals. The whole panel is ADMIN-ONLY — every tab was an
	// admin surface, so a non-admin pressing F1 gets nothing (the tabs are never built and the panel never shows).
	// Built once in BuildDebugPanel; the tabs are seeded lazily on first Admin open (the same Seed* helpers as
	// before), and re-seeded for the combat/vitals tabs on each open / replicated snapshot. (Perf is NOT here — it
	// is the standalone F3 overlay above.)
	private PanelContainer? _debugPanel;
	private TabContainer? _debugTabs;
	private bool _debugPanelVisible;
	private bool _debugFieldsSeeded;
	// The five tuning tabs (Visual/Movement/Combat/Server/Vitals) are built lazily on the first Admin open — the role
	// is unknown at construction (before login). This guards that one-time build.
	private bool _adminTabsBuilt;
	// Drag state for the movable F1 panel: the header Control reports button-down + relative motion via _GuiInput,
	// and we reposition the panel (clamped on-screen). _debugPanelDragging is true between button-down and button-up.
	private bool _debugPanelDragging;
	// FXAA/MSAA live anti-aliasing controls (Visual tab). FXAA defaults ON (mirrors the _Ready ScreenSpaceAA seed);
	// the MSAA dropdown drives GetViewport().Msaa3D. Both applied live at runtime — no restart.
	private CheckBox? _fxaaCheck;
	private OptionButton? _msaaDropdown;

	// SPEED1: the move.stepCooldownMs field was removed — the base step cooldown is now a pinned constant
	// (150 ms), not a live knob. aoi.interestRadius is the only remaining Server-tab field.
	private LineEdit? _tuneInterestRadius;

	// Visual-tab fields (was F5).
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

	// ---- S107 HUD scaffold (ui/hud) ---------------------------------------------------------------
	// The HUD is a SEPARATE CanvasLayer (Hud.tscn) from Overlay, instantiated additively in _Ready. It renders
	// ONLY from _hudState, which we refresh each frame from already-available read-only client state (local
	// position/facing — real) plus stubbed vitals/cooldowns/portrait (TODO(server)). The F5 "HUD: cycle stub
	// states" checkbox/buttons vary the stubs live so HUD states can be exercised without a server. This block is
	// the only additive HUD hook in MmoClientRoot; it touches no movement/snapshot/prediction code.
	private Mmo.Client.Godot.UI.Hud? _hud;
	private readonly Mmo.Client.Godot.UI.HudState _hudState = new();
	// Cycles the stub vitals/portrait through demo presets so a visual check can see each HUD state. Advanced by
	// the F5 "HUD: cycle stub states" button; flips values live (no restart).
	private int _hudStubPreset;
	// S109: bumped each time the static map is handed to the HUD minimap so it knows to re-bake its raster. Only
	// the wall/bounds raster keys off this; the per-frame player marker does not re-bake.
	private int _minimapGeneration;

	// ---- Movement / feel tab (was F6) -------------------------------------------------------------
	// The movement/camera-FEEL levers. All live (no restart); seeded from the current values on first open.
	// Per-entity SPEED is the "Move speed" dropdown (sends /speed); the GLOBAL base cooldown is a pinned constant
	// (SPEED1) — there is no longer a global move-speed server knob.
	// Moved from the visual surface: net latency (S93), camera follow blend + smoothing (S95).
	private LineEdit? _moveNetLatencyMs;
	private LineEdit? _moveCameraFollowBlend;
	private LineEdit? _moveCameraSmoothing;
	// New (S102): camera teleport-snap distance (tiles) — exposes the former CameraTeleportSnapTiles const live.
	private LineEdit? _moveCameraTeleportSnapTiles;
	// S106: the "Move speed" dropdown — discrete tick-quantized speeds (unnamed, numeric labels). Each item carries
	// its multiplier; selecting one sends /speed <mult> live. Populated once on first open from ServerHello (base
	// cadence + tick rate). _moveSpeedOptions is the parallel option list (item index -> SpeedOption) so the
	// selection handler can read the multiplier without re-deriving it.
	private OptionButton? _moveSpeedDropdown;
	private IReadOnlyList<MovementSpeedOptions.SpeedOption> _moveSpeedOptions = Array.Empty<MovementSpeedOptions.SpeedOption>();
	// UO4: the "Stop on reversal" (settle-then-go) toggle. The stop-on-reversal lever lives in the predictor (the
	// sole local-player render path), so it is always shown on the F6 panel.
	private CheckBox? _stopOnReversalCheck;

	// ---- Vitals tab (was F7) ----------------------------------------------------------------------
	// Set the LOCAL player's CURRENT vitals live (HP/mana/stamina). Each row is a label + LineEdit; Apply sends the
	// three current values to the server via AdminSetStat (the server admin-gates + clamps authoritatively, then
	// replicates the result back via PlayerStatsMessage so the bars track it). Re-seeded from the live replicated
	// stats on each open. Set so the bars move min/max.
	private LineEdit? _statHealthEdit;
	private LineEdit? _statManaEdit;
	private LineEdit? _statStaminaEdit;

	// ---- Combat tab (was F8) ----------------------------------------------------------------------
	// The free-aim COMBAT feel-knobs (attack cooldown, swing-root, sector half-angle/radius, damage). Each row is a
	// label + LineEdit; Apply sends each via AdminSetTuning on the combat.* registry keys (the server admin-gates +
	// clamps authoritatively, then BROADCASTS the replicated CombatTuningSnapshot back — which re-seeds these fields
	// and rebuilds the wedge/predictor/cooldown viz). Seeded on open (and on every replicated snapshot).
	private int _combatPanelSeededVersion = -1;
	private LineEdit? _combatAttackCooldownMs;
	private LineEdit? _combatRootMs;
	private LineEdit? _combatHalfAngleDeg;
	private LineEdit? _combatRadiusTiles;
	private LineEdit? _combatDamage;

	// LIVING-ENEMIES P2-POLISH: the F1 "Monster" tab — a per-TYPE dropdown + the selected type's tuning fields. Edits
	// THAT type's live (replicated) values via AdminSetTuning on "<typeId>.<field>" keys; the server clamps +
	// broadcasts the MonsterTuningSnapshot back, which re-seeds these fields (mirroring the Combat tab pattern).
	private int _monsterPanelSeededVersion = -1;
	private int _monsterSelectedTypeIndex;
	private OptionButton? _monsterTypeDropdown;
	private LineEdit? _monsterMaxHealth;
	private LineEdit? _monsterMoveSpeed;
	private LineEdit? _monsterRoamRadius;
	private LineEdit? _monsterAggroRadius;
	private LineEdit? _monsterChaseLeash;
	private LineEdit? _monsterAttackRange;
	private LineEdit? _monsterAttackDamage;
	private LineEdit? _monsterAttackCooldownMs;
	private LineEdit? _monsterPauseMinMs;
	private LineEdit? _monsterPauseMaxMs;
	private LineEdit? _monsterRespawnMs;

	private readonly ItemRegistry _itemRegistry = ItemRegistry.Default;
	private long _renderedInventoryVersion = -1;
	// LOOT P4c: the last CorpseLootVersion we rendered into the loot window, so UpdateLootWindow only rebuilds the
	// panel (open/refresh/close) when the replicated corpse contents actually changed.
	private int _renderedCorpseLootVersion = -1;
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
	// Tracks the dev/monitoring HUD (perf HUD readout + server-metrics panel + status-panel diagnostics). Kept in
	// sync with the STANDALONE F3 perf overlay's visibility (SetPerfPanelVisible): the perf HUD + graph live on that
	// overlay, the metrics panel is the right-side overlay, and the status diagnostics key off this. Hidden by
	// default so the launch screen is clean; F3 / the debug-control `client_toggle_perf` drive it.
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
		// FXAA on by default. The Compatibility renderer (we left Forward+ for its shader-compile hitches, commit
		// 66c232a) ships with NO anti-aliasing, so geometry edges crawl/shimmer as the camera moves — a subtle
		// "stutter" the timing-clean frame-log can't see. FXAA is the screen-space AA Compatibility supports (TAA is
		// Forward+-only). Runtime-applied so project.godot isn't re-dirtied. The F1 tuning panel's Visual tab
		// now owns the live on/off (this checkbox reflects + drives it) + an MSAA option (MSAA is sharper for
		// edge-crawl if FXAA's blur is too soft). Seed the checkbox to match this default-ON state.
		GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa;
		_fxaaCheck?.SetPressedNoSignal(true);
		_client = new MmoClient(new ClientConnectionOptions(Host, Port, ConnectionKey, PlayerName, PlayerName, "mmo-godot-client"));
		_client.Connect();
		GD.Print($"Godot MMO client connecting to {Host}:{Port} as {PlayerName}.");

		_controlChannel = DebugControlChannel.TryCreate(this);

		// S107: mount the HUD as a separate CanvasLayer (additive — see the HUD scaffold field block). Failing to
		// load the scene must not break the client, so log + continue if the .tscn is missing/unimported.
		MountHud();
	}

	// S107: instantiate Hud.tscn and add it as a child. Additive only — no movement/snapshot wiring. The HUD then
	// renders from _hudState, refreshed each frame in RefreshHud (called from _Process AFTER SampleMotionMetrics).
	private void MountHud()
	{
		var hudScene = GD.Load<PackedScene>("res://UI/Hud.tscn");
		if (hudScene?.Instantiate() is not Mmo.Client.Godot.UI.Hud hud)
		{
			GD.PushWarning("S107 HUD: res://UI/Hud.tscn failed to load/instantiate; HUD not mounted.");
			return;
		}

		_hud = hud;
		AddChild(_hud);

		// LOOT P4c: wire the loot window's take/loot-all/close intents to the client send methods. The window carries
		// the open corpse's network id so the server can guard a stale window. Hooked once at mount.
		if (_hud.Loot is { } lootWindow)
		{
			lootWindow.TakeItemRequested += templateKey => _client?.SendLootItem(lootWindow.CorpseNetworkId, templateKey);
			lootWindow.LootAllRequested += () => _client?.SendLootAll(lootWindow.CorpseNetworkId);
			lootWindow.CloseRequested += () => _client?.SendCloseLoot(lootWindow.CorpseNetworkId);
		}
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
		UpdateFloatingDamageNumbers(delta);
		var t2 = Time.GetTicksUsec();
		UpdateCamera();
		UpdatePredictionTileMarkers();
		UpdateMonsterHomeMarkers();
		UpdateAimWedge();
		UpdateLocalContinuousFacing();
		var t3 = Time.GetTicksUsec();
		UpdateOverlay(now);
		var t4 = Time.GetTicksUsec();

		RecordSectionTiming(pollUsec, t1 - t0, t2 - t1, t3 - t2, t4 - t3);
		SampleMotionMetrics();
		// S109 read-order fix (carried-forward S107 note): feed the HUD AFTER SampleMotionMetrics so the minimap
		// consumes THIS frame's fresh local render position/facing instead of last frame's. Only the additive HUD
		// feed call moved here from UpdateOverlay's tail — no movement/snapshot computation changed.
		RefreshHud();
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

	// Tab = toggle the Inventory window. Handled in _Input (which runs BEFORE Godot's GUI focus navigation,
	// where Tab is bound to ui_focus_next) so Tab reliably opens/closes the inventory instead of cycling
	// control focus. Ignored while typing in chat so Tab behaves normally in the chat box.
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false } key
			&& key.Keycode == Key.Tab
			&& _chatInput?.HasFocus() != true)
		{
			_hud?.ToggleInventory();
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// S56: mouse movement is now hold-to-walk-toward-cursor (UO control), polled every frame in
		// SendHeldMovement off Input.IsMouseButtonPressed — NOT an event-driven click-a-destination. So the
		// right mouse button is intentionally not consumed here; the old HandleClickToMove path is retired.
		// Mouse-wheel zoom: shrink/grow the orthographic camera around the character.
		if (@event is InputEventMouseButton { Pressed: true } mouseButton)
		{
			if (mouseButton.ButtonIndex == MouseButton.WheelUp)
			{
				_cameraSize = Mathf.Clamp(_cameraSize - CameraZoomStep, _cameraSizeMin, _cameraSizeMax);
				GetViewport().SetInputAsHandled();
				return;
			}
			if (mouseButton.ButtonIndex == MouseButton.WheelDown)
			{
				_cameraSize = Mathf.Clamp(_cameraSize + CameraZoomStep, _cameraSizeMin, _cameraSizeMax);
				GetViewport().SetInputAsHandled();
				return;
			}

			// COMBAT (LMB attack): LEFT-mouse-down triggers the free-aim melee swing — the same TryAttack path as
			// Space (server-authoritative; the aim is the player→cursor bearing). This handler is _UnhandledInput, so
			// any HUD/panel control the cursor is over has already consumed the click (it never reaches here) — the
			// swing only fires on a click into the 3D world. RIGHT mouse stays the hold-to-move poll (untouched);
			// LEFT was previously free. Consumed so it doesn't fall through to anything else.
			if (mouseButton.ButtonIndex == MouseButton.Left)
			{
				TryAttack();
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		if (@event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		// F1: toggle the ADMIN tuning panel (the five-tab TabContainer: Visual/Movement/Combat/Server/Vitals).
		// Admin-only — a non-admin press is a no-op (the tabs are never built, ToggleDebugPanel short-circuits).
		if (key.Keycode == Key.F1)
		{
			ToggleDebugPanel();
			GetViewport().SetInputAsHandled();
			return;
		}

		// F3: toggle the standalone PERF HUD overlay (the old F3 perf overlay — glanceable while playing). For
		// EVERYONE (it was the non-admin overlay). OpenDebugPanelOnPerfTab / TogglePerfPanel back the
		// client_toggle_perf control-channel command.
		if (key.Keycode == Key.F3)
		{
			TogglePerfPanel();
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
			DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
				? DisplayServer.WindowMode.Windowed
				: DisplayServer.WindowMode.Fullscreen);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (key.Keycode == Key.Escape && _chatInput?.HasFocus() == true)
		{
			_chatInput.ReleaseFocus();
			GetViewport().SetInputAsHandled();
			return;
		}

		// LOOT P4c: Escape closes the loot window when it's open (and chat isn't focused). Raises the window's close
		// (which tells the server to forget the open-loot pairing + hides the panel locally).
		if (key.Keycode == Key.Escape && _chatInput?.HasFocus() != true && _hud?.Loot is { IsOpen: true } lootWindow)
		{
			lootWindow.RaiseCloseRequested();
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

		// COMBAT-S2B: Space = melee attack. A clear, free key (no collision with WASD movement, F3-F7/F11 panels,
		// E harvest, Tab inventory, Alt+R resync, or Enter/T chat). Not while typing in chat (so Space types a space
		// in a message instead of swinging). Sends the attack on its own cursor (MmoClient.SendAttack) and shows an
		// immediate cosmetic swing cue; the server authoritatively resolves the cone + damage (the target's overhead
		// HP bar drops via the snapshot — no client-side damage prediction).
		if (key.Keycode == Key.Space && _chatInput?.HasFocus() != true)
		{
			TryAttack();
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

	// FREEAIM: send a free-aim melee attack and give immediate cosmetic feedback. The attack rides its OWN cursor
	// (MmoClient.SendAttack) and is server-authoritative — the client predicts only the FEEL (the wedge telegraph),
	// never the damage. The aim is the CONTINUOUS player→cursor world bearing (NOT a Direction8), quantized via the
	// shared AimAngle so it decodes to the same radians the server resolves the sector with. The actual hit/HP drop
	// confirms via the snapshot stream (the target's overhead bar) — no client-side damage prediction.
	private void TryAttack()
	{
		if (_client?.IsLoggedIn != true)
		{
			return;
		}

		// Gate locally on the SAME attack cooldown the radial shows (and the server enforces): spamming the key or
		// LMB must not fire a wedge + reset the cooldown indicator on every press. The server stays authoritative on
		// damage (TryBeginAttack); this keeps the client feel + the radial honest and avoids sending attacks the
		// server would just reject. A press near the boundary the server later rejects is a harmless wasted swing.
		if (_client.AttackCooldownRemainingFraction(out _) > 0d)
		{
			return;
		}

		// Aim continuously toward the cursor's ground point. Falls back to the local player's discrete facing only
		// if the cursor pick fails (no camera / ray miss), so a swing always has a defined aim.
		var aimRadians = TryGetAimToCursor(out var cursorAim)
			? cursorAim
			: LocalFacingRadians();

		_client.SendAttack(AimAngle.Quantize(aimRadians));
		ShowInteractFeedback("Swing!");

		if (TryGetLocalRenderPosition(out var px, out var pz))
		{
			// Flash the free-aim WEDGE (a pie slice: FreeAimHalfAngle, FreeAimRadius) on the ground from the local
			// player, oriented along the aim — the danger area the server resolves. Purely cosmetic; the authoritative
			// hit still confirms via the HP snapshot.
			FlashAimWedge(new Vector3(px, 0f, pz), aimRadians);

			// FREEAIM-PREDICT: pop the damage number INSTANTLY (no server round-trip) over every rendered enemy the
			// swing should hit, mirroring movement prediction. Uses the SHARED FreeAimSector.IsHit (identical geometry
			// to the server resolver) against each enemy's RENDER position, the local render position as the attacker,
			// and the REPLICATED combat tuning. Cosmetic only — the authoritative HP still rides the snapshot, and the
			// server suppresses its own DamageEventMessage to THIS attacker so the number isn't doubled. An occasional
			// mispredict (a stray/missing number when render positions disagree with the server tile) is acceptable.
			PredictSwingDamageNumbers(px, pz, aimRadians);
		}
	}

	// FREEAIM-PREDICT: predict + pop the attacker's own damage numbers for a swing from (attackerX, attackerZ) along
	// `aimRadians`. Mirrors the server's no-friendly-fire gate (Dummy/Npc only, skip self) and reuses the SHARED
	// FreeAimSector.IsHit with the replicated half-angle/radius/damage + the shared body radius, so the predicted
	// hit/miss matches the server's resolution. No-op before the first replicated CombatTuningSnapshot arrives.
	private void PredictSwingDamageNumbers(float attackerX, float attackerZ, double aimRadians)
	{
		if (_client?.CombatTuning is not { } tuning || _floatingText is null || _renderer is null)
		{
			return;
		}

		var localId = _client.LocalNetworkId;
		foreach (var state in _renderStates)
		{
			// No friendly fire: only Dummy/Npc/Monster are damageable; never the local player / self.
			if (state.IsLocal || state.NetworkId == localId)
			{
				continue;
			}

			if (state.Kind is not (EntityKind.Dummy or EntityKind.Npc or EntityKind.Monster))
			{
				continue;
			}

			if (!FreeAimSector.IsHit(
					attackerX,
					attackerZ,
					aimRadians,
					tuning.HalfAngleRadians,
					tuning.RadiusTiles,
					FreeAimSector.EntityHitRadiusTiles,
					state.Position.X,
					state.Position.Y))
			{
				continue;
			}

			// Pop the number at the victim's live visual (same path/position as the server-driven number). Fall back
			// to the render-state XZ if no visual is bound this frame, so the prediction still shows.
			if (_renderer.TryGetActiveVisual(state.NetworkId, out var visual))
			{
				_floatingText.Spawn(visual.Position, tuning.Damage);
			}
			else
			{
				_floatingText.Spawn(new Vector3((float)state.Position.X, 0f, (float)state.Position.Y), tuning.Damage);
			}
		}
	}

	// FREEAIM: the continuous player→cursor world bearing in radians (atan2(dz, dx), +X east / +Z south — the same
	// convention the shared AimAngle uses and the server's sector resolver reduces against). Returns false before
	// login, when there is no local render position yet, or when the ground ray misses; in the dead-zone (cursor on
	// the player) it still returns a (possibly noisy) bearing — an attack always has an aim, unlike movement which
	// stops in the dead-zone.
	private bool TryGetAimToCursor(out float radians)
	{
		radians = 0f;
		if (_client?.IsLoggedIn != true)
		{
			return false;
		}

		if (!TryGetLocalRenderPosition(out var playerX, out var playerZ))
		{
			return false;
		}

		var screenPosition = GetViewport().GetMousePosition();
		if (!TryPickGroundPoint(screenPosition, out var hit))
		{
			return false;
		}

		var dx = hit.X - playerX;
		var dz = hit.Z - playerZ;
		if (Mathf.IsZeroApprox(dx) && Mathf.IsZeroApprox(dz))
		{
			return false;
		}

		radians = Mathf.Atan2(dz, dx);
		return true;
	}

	// Fallback aim when the cursor pick fails: the local player's discrete 8-way facing as a world bearing (same
	// atan2(delta.Y, delta.X) convention). Defaults to east if facing is unknown.
	private float LocalFacingRadians()
	{
		foreach (var state in _renderStates)
		{
			if (state.IsLocal)
			{
				var delta = state.Facing.Delta();
				if (delta.X != 0 || delta.Y != 0)
				{
					return Mathf.Atan2(delta.Y, delta.X);
				}
			}
		}

		return 0f;
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
		// COMBAT-QOL: floating damage numbers live under the SAME entity root so a victim visual's local Position is
		// the world anchor to spawn the number at.
		_floatingText = new FloatingTextManager(_entityRoot);

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
		// Sits above the chat-input bar (which is at OffsetTop -42). Tall enough for the 8 log lines + the "CHAT"
		// header (~190px) so the newest line never spills down onto the input bar.
		chatPanel.OffsetTop = -260f;
		chatPanel.OffsetBottom = -70f;
		var chatRows = CreatePanelVBox(chatPanel);
		_chatLabel = CreateOverlayLabel("Chat", 14);
		chatRows.AddChild(_chatLabel);

		// The perf HUD readout (_perfLabel) + FrameTimeGraph (_perfGraph) + the perf-diagnostic toggles live on the
		// STANDALONE F3 perf overlay (BuildPerfPanel) — separate from the F1 tuning panel. See that method.

		// S111: the old top-right text inventory panel (S39) was REPLACED by the toggleable Inventory window
		// (UI/InventoryWindow, mounted on the Hud CanvasLayer). The same owner-only InventoryUpdate data now
		// flows through UpdateInventory() -> _hud.SetInventory() — see UpdateInventory below. No panel here.

		// Interact feedback toast: bottom-center, above the chat panel. Brief, auto-hiding.
		var toastPanel = CreateOverlayPanel("ToastPanel", Vector2.Zero, new Vector2(420, 36));
		toastPanel.AnchorLeft = 0.5f;
		toastPanel.AnchorRight = 0.5f;
		toastPanel.AnchorTop = 1f;
		toastPanel.AnchorBottom = 1f;
		toastPanel.OffsetLeft = -210f;
		toastPanel.OffsetRight = 210f;
		toastPanel.OffsetTop = -310f;
		toastPanel.OffsetBottom = -274f;
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

		BuildPerfPanel(layer);
		BuildDebugPanel(layer);

		// Dev/monitoring overlays start hidden — F3 reveals the perf overlay; the server-metrics panel rides
		// _debugOverlayVisible with it. The status panel stays visible but shows only a minimal always-on line until
		// the perf overlay is on. F1 reveals the admin tuning panel (independent of _debugOverlayVisible).
		metricsPanel.Visible = false;

		layer.AddChild(statusPanel);
		layer.AddChild(metricsPanel);
		layer.AddChild(chatPanel);
		layer.AddChild(toastPanel);
		layer.AddChild(inputPanel);
	}

	// The STANDALONE F3 perf overlay (restored from before the panel consolidation): a glanceable HUD you watch
	// WHILE playing — the perf readout label (_perfLabel) + the frame-time graph (_perfGraph) + the two perf
	// diagnostic toggles (uncap-fps, frame-log CSV). Available to EVERYONE (it was the non-admin overlay). Its own
	// PanelContainer, NOT a tab on the F1 tuning panel and NOT draggable. Toggled by F3 / client_toggle_perf.
	private void BuildPerfPanel(CanvasLayer layer)
	{
		var panel = CreateOverlayPanel("PerfPanel", new Vector2(490, 154), new Vector2(470, 240));
		var rows = CreatePanelVBox(panel);

		_perfLabel = CreateOverlayLabel("PerfHud", 13);
		_perfGraph = new FrameTimeGraph
		{
			Name = "PerfFrameGraph",
			CustomMinimumSize = new Vector2(436, 78)
		};
		rows.AddChild(_perfLabel);
		rows.AddChild(_perfGraph);

		// Perf diagnostics (perf knobs, not render knobs — they live with the perf HUD).
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

		panel.Visible = false;
		_perfPanel = panel;
		layer.AddChild(panel);
	}

	// The F1 ADMIN tuning panel. One TabContainer holding the five tuning surfaces (the old F4–F8 F-key panels) as
	// thematic tabs: Visual / Movement / Combat / Server / Vitals. Every control migrated VERBATIM from the old
	// Build* methods — same labels, names, Apply*/handler wiring, live behavior. The whole panel is ADMIN-ONLY (all
	// five tabs were admin surfaces): the tabs are built lazily on the first Admin open and SetDebugPanelVisible
	// short-circuits for a non-admin, so a non-admin F1 press does nothing. The panel is MOVABLE via a header
	// drag-handle and is sized large (860×680) to comfortably show the busiest tab. Built once here; seeded lazily.
	private void BuildDebugPanel(CanvasLayer layer)
	{
		var panel = CreateOverlayPanel("DebugPanel", new Vector2(360, 80), new Vector2(860, 680));
		// The panel content (header + tabs) fills the panel; the TabContainer + each tab's ScrollContainer expand to
		// fill the larger size so the busiest tab's controls have room.
		var outer = new VBoxContainer { Name = "Outer", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		outer.AddThemeConstantOverride("separation", 4);
		var margin = CreatePanelMargin(panel);
		margin.AddChild(outer);

		// Drag handle: a "Debug" header bar at the top. Mouse drag on it repositions the whole panel (clamped
		// on-screen) — standard in-game-panel drag. Wired via _GuiInput on the header Control (OnDebugHeaderGuiInput).
		var header = new PanelContainer { Name = "DebugHeader", MouseFilter = Control.MouseFilterEnum.Stop };
		var headerStyle = new StyleBoxFlat { BgColor = new Color(0.10f, 0.16f, 0.20f, 0.85f) };
		headerStyle.SetCornerRadiusAll(4);
		headerStyle.SetContentMarginAll(4);
		header.AddThemeStyleboxOverride("panel", headerStyle);
		var headerLabel = CreateOverlayLabel("DebugHeaderLabel", 14);
		headerLabel.Text = "Debug — drag to move";
		headerLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
		header.AddChild(headerLabel);
		header.GuiInput += OnDebugHeaderGuiInput;
		outer.AddChild(header);

		var tabs = new TabContainer { Name = "DebugTabs", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		tabs.AddThemeFontSizeOverride("font_size", 14);
		outer.AddChild(tabs);
		_debugTabs = tabs;

		// The five tuning tabs (the old admin-only F4–F8 panels) are built LAZILY on first Admin open
		// (EnsureAdminTabsBuilt) — at construction (_Ready, before login) the role is unknown. A non-admin never
		// triggers the build (and SetDebugPanelVisible short-circuits), so a non-admin never sees the panel.

		panel.Visible = false;
		_debugPanel = panel;
		layer.AddChild(panel);
	}

	// Header drag-handle input: on left button-down begin dragging; on motion while dragging, slide the panel by the
	// mouse delta and clamp it fully on-screen; on button-up end the drag. Repositions only the F1 panel (the F3 perf
	// overlay is not movable).
	private void OnDebugHeaderGuiInput(InputEvent @event)
	{
		if (_debugPanel is null)
		{
			return;
		}

		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
		{
			_debugPanelDragging = mb.Pressed;
			return;
		}

		if (@event is InputEventMouseMotion motion && _debugPanelDragging)
		{
			var viewport = GetViewport().GetVisibleRect().Size;
			var size = _debugPanel.Size;
			var pos = _debugPanel.Position + motion.Relative;
			// Clamp so the panel stays fully on-screen (top-left within [0, viewport - size]).
			var maxX = Mathf.Max(0f, viewport.X - size.X);
			var maxY = Mathf.Max(0f, viewport.Y - size.Y);
			pos.X = Mathf.Clamp(pos.X, 0f, maxX);
			pos.Y = Mathf.Clamp(pos.Y, 0f, maxY);
			_debugPanel.Position = pos;
		}
	}

	// One tab page: a ScrollContainer (so a long tab scrolls) wrapping a VBox of rows. The page Control's Name is
	// the TAB TITLE shown on the tab bar. The ScrollContainer expands to fill the (large) TabContainer. Returns the
	// VBox the caller fills with the migrated rows.
	private static VBoxContainer AddDebugTab(TabContainer tabs, string title)
	{
		var scroll = new ScrollContainer { Name = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		var rows = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		rows.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(rows);
		tabs.AddChild(scroll);
		return rows;
	}

	// Lazily build the five admin tuning tabs (the old F4–F8 panels) the first time an Admin opens the F1 panel:
	// Visual / Movement / Combat / Server / Vitals. Built once (idempotent via _adminTabsBuilt) and only for an Admin
	// session — a non-admin never gets them (and never sees the panel). Every row is migrated verbatim from the old
	// Build* method.
	private void EnsureAdminTabsBuilt()
	{
		if (_adminTabsBuilt || _debugTabs is null || _client?.Role != ClientRole.Admin)
		{
			return;
		}

		BuildVisualTab(_debugTabs);
		BuildMovementTab(_debugTabs);
		BuildCombatTab(_debugTabs);
		BuildMonsterTab(_debugTabs);
		BuildServerTab(_debugTabs);
		BuildVitalsTab(_debugTabs);
		_adminTabsBuilt = true;
	}

	// Visual tab (was F5): camera zoom range, rock/tree/plant model scale, label pixel-size/height, the live Cato
	// placement fields, the debug-facing-box / Cato-sprite / prediction-tiles toggles, the HUD stub cycler, and the
	// NEW anti-aliasing controls (FXAA on/off + an MSAA dropdown). All applied INSTANTLY client-side on Apply (the
	// fields) or on click (the toggles) — same as the old F5 panel. (The uncap-fps + frame-log toggles live on the
	// standalone F3 perf overlay; the movement/feel levers are on the Movement tab.)
	private void BuildVisualTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Visual");

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
		// S99: live Cato sprite placement. Applied INSTANTLY client-side on Apply/Enter (no respawn): pushed onto
		// every active Cato visual via the renderer. Scale (px size) 2× the S96 first-guess by default; the Y/X
		// offsets centre the cat body on the tile (the frame centre sits above the cat, wand extending up-right).
		_tuneCatoPixelSize = AddTuningField(rows, "Cato scale (px size)", OnVisualApplyPressed);
		_tuneCatoYOffset = AddTuningField(rows, "Cato Y offset", OnVisualApplyPressed);
		_tuneCatoXOffset = AddTuningField(rows, "Cato X offset", OnVisualApplyPressed);
		// S101: toward-camera depth — slides Cato along the ground-projected camera direction (1,0,1)/√2,
		// positive = toward the camera. Live-applied like the other Cato fields (no respawn).
		_tuneCatoDepth = AddTuningField(rows, "Cato depth (toward cam)", OnVisualApplyPressed);

		// NEW anti-aliasing controls (live, no restart). FXAA on/off mirrors + drives GetViewport().ScreenSpaceAA
		// (defaults ON, seeded in _Ready). The MSAA dropdown drives GetViewport().Msaa3D (Disabled/2x/4x/8x). Both
		// flip on click — no Apply needed.
		var aaHeader = CreateOverlayLabel("VisualAaHeader", 12);
		aaHeader.Text = "— anti-aliasing (instant) —";
		rows.AddChild(aaHeader);

		var fxaa = new CheckBox { Name = "Fxaa", Text = "FXAA (screen-space AA)", ButtonPressed = GetViewport().ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Fxaa };
		fxaa.AddThemeFontSizeOverride("font_size", 13);
		fxaa.Toggled += ApplyFxaa;
		rows.AddChild(fxaa);
		_fxaaCheck = fxaa;

		var msaaRow = new HBoxContainer { Name = "Row_Msaa" };
		msaaRow.AddThemeConstantOverride("separation", 8);
		var msaaCaption = CreateOverlayLabel("Cap_Msaa", 13);
		msaaCaption.Text = "MSAA (3D)";
		msaaCaption.CustomMinimumSize = new Vector2(170, 0);
		msaaRow.AddChild(msaaCaption);
		_msaaDropdown = new OptionButton { Name = "MsaaDropdown", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_msaaDropdown.AddThemeFontSizeOverride("font_size", 13);
		// Item indices map to the Viewport.Msaa values in ApplyMsaaSelected. Disabled is the live default.
		_msaaDropdown.AddItem("Disabled", 0);
		_msaaDropdown.AddItem("2x", 1);
		_msaaDropdown.AddItem("4x", 2);
		_msaaDropdown.AddItem("8x", 3);
		_msaaDropdown.Select(MsaaIndexFor(GetViewport().Msaa3D));
		_msaaDropdown.ItemSelected += ApplyMsaaSelected;
		msaaRow.AddChild(_msaaDropdown);
		rows.AddChild(msaaRow);

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

		// Spawner tiles: debug viz of the monster spawner anchors (red tiles), default off — like prediction tiles.
		var spawnerTiles = new CheckBox { Name = "SpawnerTiles", Text = "Spawner tiles", ButtonPressed = _showSpawnerTiles };
		spawnerTiles.AddThemeFontSizeOverride("font_size", 13);
		spawnerTiles.Toggled += ApplySpawnerTiles;
		rows.AddChild(spawnerTiles);

		// S107 HUD scaffold — live debug control (no Apply, no restart, no launch flag, per the live-toggle rule).
		// Each press cycles the STUBBED HudState (health/resource/portrait/cooldowns) through demo presets so the
		// HUD states can be exercised without a server. Mutates only stub fields; never touches movement state.
		var hudCycle = new Button { Name = "HudCycleStub", Text = "HUD: cycle stub states" };
		hudCycle.AddThemeFontSizeOverride("font_size", 13);
		hudCycle.Pressed += CycleHudStubState;
		rows.AddChild(hudCycle);

		var apply = new Button { Name = "VisualApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnVisualApplyPressed;
		rows.AddChild(apply);
	}

	// Movement tab (was F6): the movement/camera-FEEL levers — Move speed dropdown, net latency, camera follow
	// blend + smoothing + teleport-snap, the stop-on-reversal toggle, and the Force Resync button. All live (no
	// restart) via the same Apply-all / live-toggle pattern. Verbatim from the old F6 panel.
	private void BuildMovementTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Movement");

		var note = CreateOverlayLabel("MovementSpeedNote", 12);
		note.Text = "— client-local (instant) —";
		rows.AddChild(note);

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

		// Applied on Apply/Enter:
		// S93: artificial one-way network latency (ms each way). 0 = off (default I/O path). Felt RTT ≈ 2× this.
		_moveNetLatencyMs = AddTuningField(rows, "Net latency (ms, each way)", OnMovementApplyPressed);
		// S95: camera focus blend between the confirmed tile (0) and the rendered character (1, default).
		_moveCameraFollowBlend = AddTuningField(rows, "Camera follow blend (0=tile,1=char)", OnMovementApplyPressed);
		// S95: camera follow smoothing as a per-second rate (frame-rate independent). 0 = off/hard-follow.
		_moveCameraSmoothing = AddTuningField(rows, "Camera smoothing (/s, 0=off)", OnMovementApplyPressed);
		// S102 new: camera teleport-snap distance (tiles). Beyond this single-frame jump the camera hard-snaps
		// (respawn / zone change) instead of gliding; below it the smoothing glides. Was the const = 4.
		_moveCameraTeleportSnapTiles = AddTuningField(rows, "Camera teleport-snap (tiles)", OnMovementApplyPressed);

		// UO4 live toggle — flips on click, no Apply needed: settle-then-go on a ~180° reversal. ON = a 180°
		// flip while moving settles to a clean tile stop, then resumes the new direction (kills the left-right
		// bounce). OFF (default) = the current immediate reverse. The predictor (the sole local-player render path)
		// reads it.
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

		// CAMERA-EXPERIMENT live toggle — flips on click, no Apply: the camera targets the DISCRETE predicted tile
		// instead of the smooth character render. Pair with "Camera smoothing" > 0 to glide the tile-to-tile jumps
		// (smoothing 0 makes it hard-jump per step). OFF (default) = follow the character.
		var trackPredictedTile = new CheckBox
		{
			Name = "CameraTrackPredictedTile",
			Text = "Camera: track predicted tile",
			ButtonPressed = _cameraTrackPredictedTile
		};
		trackPredictedTile.AddThemeFontSizeOverride("font_size", 13);
		trackPredictedTile.Toggled += ApplyCameraTrackPredictedTile;
		rows.AddChild(trackPredictedTile);

		// RESYNC1: manual Force Resync button. Calls MmoClient.ForceResync() -> LocalPlayerPredictor.ForceResync(),
		// which hard-snaps the local prediction (tile, step-seq, render) onto the last server-confirmed position and
		// clears any stranded in-flight lead. USER-TRIGGERED escape hatch for a loss-induced desync; the same primitive
		// as the Alt+R hotkey, and the one UO5/NET4 auto-tiers will call. Live, no restart.
		var forceResync = new Button { Name = "ForceResync", Text = "Force Resync" };
		forceResync.AddThemeFontSizeOverride("font_size", 14);
		forceResync.Pressed += () => _client?.ForceResync();
		rows.AddChild(forceResync);

		var apply = new Button { Name = "MovementApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnMovementApplyPressed;
		rows.AddChild(apply);
	}

	// Combat tab (was F8): the free-aim combat feel-knobs (attack cooldown ms, swing-root ms, sector half-angle deg,
	// radius tiles, damage). Apply sends each via AdminSetTuning on the combat.* keys; the server clamps + broadcasts
	// the replicated snapshot back, which re-seeds the fields + rebuilds the wedge/predictor/cooldown viz. Verbatim.
	private void BuildCombatTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Combat");

		var header = CreateOverlayLabel("CombatHeader", 12);
		header.Text = "— server-authoritative · replicated · sent on Apply —";
		rows.AddChild(header);

		_combatAttackCooldownMs = AddTuningField(rows, "attack cooldown (ms)", OnCombatApplyPressed);
		_combatRootMs = AddTuningField(rows, "swing root (ms)", OnCombatApplyPressed);
		_combatHalfAngleDeg = AddTuningField(rows, "half-angle (deg)", OnCombatApplyPressed);
		_combatRadiusTiles = AddTuningField(rows, "radius (tiles)", OnCombatApplyPressed);
		_combatDamage = AddTuningField(rows, "damage (hp)", OnCombatApplyPressed);

		var apply = new Button { Name = "CombatApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnCombatApplyPressed;
		rows.AddChild(apply);
	}

	// LIVING-ENEMIES P2-POLISH: the "Monster" tab. A per-TYPE dropdown at the top (just "Slime" now) + the selected
	// type's tuning fields below. Apply sends each via AdminSetTuning on "<typeId>.<field>" keys (e.g. slime.roamRadius);
	// the server admin-gates + clamps + broadcasts the MonsterTuningSnapshot back, which re-seeds the fields. The
	// dropdown is populated + the fields seeded from MmoClient.MonsterTuning (the replicated per-type values).
	private void BuildMonsterTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Monster");

		var header = CreateOverlayLabel("MonsterHeader", 12);
		header.Text = "— server-authoritative · per-type · sent on Apply —";
		rows.AddChild(header);

		// The type dropdown. Selecting a type re-seeds the fields below from that type's replicated values.
		var typeRow = new HBoxContainer { Name = "Row_MonsterType" };
		typeRow.AddThemeConstantOverride("separation", 8);
		var typeCaption = CreateOverlayLabel("Cap_MonsterType", 13);
		typeCaption.Text = "type";
		typeCaption.CustomMinimumSize = new Vector2(170, 0);
		typeRow.AddChild(typeCaption);
		_monsterTypeDropdown = new OptionButton { Name = "MonsterTypeDropdown", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_monsterTypeDropdown.AddThemeFontSizeOverride("font_size", 13);
		_monsterTypeDropdown.ItemSelected += OnMonsterTypeSelected;
		typeRow.AddChild(_monsterTypeDropdown);
		rows.AddChild(typeRow);

		// The selected type's fields. Labels match the per-type field meaning; Apply maps each to a "<typeId>.<field>".
		_monsterMaxHealth = AddTuningField(rows, "hp (max)", OnMonsterApplyPressed);
		_monsterMoveSpeed = AddTuningField(rows, "move speed (x)", OnMonsterApplyPressed);
		_monsterRoamRadius = AddTuningField(rows, "roam radius", OnMonsterApplyPressed);
		_monsterAggroRadius = AddTuningField(rows, "aggro radius", OnMonsterApplyPressed);
		_monsterChaseLeash = AddTuningField(rows, "chase leash", OnMonsterApplyPressed);
		_monsterAttackRange = AddTuningField(rows, "attack range", OnMonsterApplyPressed);
		_monsterAttackDamage = AddTuningField(rows, "attack damage", OnMonsterApplyPressed);
		_monsterAttackCooldownMs = AddTuningField(rows, "attack cooldown (ms)", OnMonsterApplyPressed);
		_monsterPauseMinMs = AddTuningField(rows, "pause min (ms)", OnMonsterApplyPressed);
		_monsterPauseMaxMs = AddTuningField(rows, "pause max (ms)", OnMonsterApplyPressed);
		_monsterRespawnMs = AddTuningField(rows, "respawn (ms)", OnMonsterApplyPressed);

		var apply = new Button { Name = "MonsterApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnMonsterApplyPressed;
		rows.AddChild(apply);
	}

	// Server tab (was F4): the server-side tuning knobs — aoi.interestRadius. Apply sends every server field via
	// AdminSetTuning (the server admin-gates + clamps authoritatively). Verbatim from the old F4 panel.
	private void BuildServerTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Server");

		var serverHeader = CreateOverlayLabel("TuningServerHeader", 12);
		serverHeader.Text = "— server (sent on Apply) —";
		rows.AddChild(serverHeader);
		_tuneInterestRadius = AddTuningField(rows, "aoi.interestRadius", OnTuningApplyPressed);

		var apply = new Button { Name = "TuningApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnTuningApplyPressed;
		rows.AddChild(apply);
	}

	// Vitals tab (was F7): set the local player's current HP/mana/stamina live. Apply sends each via AdminSetStat
	// (the server admin-gates + clamps, then replicates the result back so the bars track it). Verbatim from F7.
	private void BuildVitalsTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Vitals");

		var header = CreateOverlayLabel("StatHeader", 12);
		header.Text = "— local player · current value · sent on Apply —";
		rows.AddChild(header);

		_statHealthEdit = AddTuningField(rows, "hp (current)", OnStatApplyPressed);
		_statManaEdit = AddTuningField(rows, "mana (current)", OnStatApplyPressed);
		_statStaminaEdit = AddTuningField(rows, "stamina (current)", OnStatApplyPressed);

		var apply = new Button { Name = "StatApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnStatApplyPressed;
		rows.AddChild(apply);
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
	// server recomputes the effective cadence and replies with MovementSpeedChanged, which retunes the local
	// predictor via EntityState.SetStepCooldownMs — so the avatar's step rate tracks the new cadence live.
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

	// UO4 F6 live toggle ("Stop on reversal"). Route the flag to the client (and the active predictor) immediately
	// — no restart. ON = settle-then-go on a ~180° reversal; OFF = the current immediate reverse.
	private void ApplyStopOnReversal(bool enabled)
	{
		_client?.SetStopOnReversal(enabled);
	}

	// CAMERA-EXPERIMENT live toggle ("Camera: track predicted tile"). Flips the camera target between the discrete
	// predicted tile and the smooth character render (read in UpdateCamera). Client-local; no restart.
	private void ApplyCameraTrackPredictedTile(bool enabled)
	{
		_cameraTrackPredictedTile = enabled;
	}

	// Live FXAA toggle (Visual tab "FXAA" checkbox). Flips GetViewport().ScreenSpaceAA between Fxaa (on) and
	// Disabled (off) at runtime — no restart. Defaults ON (seeded in _Ready + reflected by the checkbox). FXAA is
	// the screen-space AA the Compatibility renderer supports; it composes independently of the MSAA 3D option.
	private void ApplyFxaa(bool enabled)
	{
		GetViewport().ScreenSpaceAA = enabled
			? Viewport.ScreenSpaceAAEnum.Fxaa
			: Viewport.ScreenSpaceAAEnum.Disabled;
	}

	// Live MSAA toggle (Visual tab "MSAA" dropdown). Maps the item index to a Viewport.Msaa level and applies it to
	// GetViewport().Msaa3D at runtime — no restart. Disabled (0) is the default; 2x/4x/8x trade fill cost for sharper
	// edges (an alternative to FXAA's blur for edge-crawl). Independent of the FXAA checkbox.
	private void ApplyMsaaSelected(long index)
	{
		GetViewport().Msaa3D = index switch
		{
			1 => Viewport.Msaa.Msaa2X,
			2 => Viewport.Msaa.Msaa4X,
			3 => Viewport.Msaa.Msaa8X,
			_ => Viewport.Msaa.Disabled,
		};
	}

	// Maps a live Viewport.Msaa level back to the MSAA dropdown's item index, so the dropdown seeds to the current
	// state on build (Disabled by default).
	private static int MsaaIndexFor(Viewport.Msaa msaa)
	{
		return msaa switch
		{
			Viewport.Msaa.Msaa2X => 1,
			Viewport.Msaa.Msaa4X => 2,
			Viewport.Msaa.Msaa8X => 3,
			_ => 0,
		};
	}

	// Live vsync / fps toggle, shared by the Perf-tab checkbox and MMO_UNCAP_FPS. Uncapped = vsync off + no fps cap
	// (perf testing — watch the true fps in the perf HUD); capped = vsync on. Engine.MaxFps stays 0 either way;
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

	// F1 Visual "Spawner tiles" live toggle — show/hide the red spawner-anchor debug markers (default off). The
	// rendering is gated in UpdateMonsterHomeMarkers (which frees the markers next frame when off); this just flips
	// the flag.
	private void ApplySpawnerTiles(bool enabled)
	{
		_showSpawnerTiles = enabled;
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

		// S112: paint the textured tile floor from the design bitmap. Visual-only — additive over the solid ground
		// box (which picking still needs). The floor is partitioned into a grid of CHUNK_TILES-square chunks (see
		// TerrainPainter.BuildFloor) so each chunk frustum-culls independently. When it builds, the painted tiles
		// are the new look so the procedural grid plane is hidden; if textures can't load, BuildFloor returns null
		// and we keep the grid visible as a graceful fallback.
		var paintedFloor = Mmo.Client.Godot.Visuals.TerrainPainter.BuildFloor(_worldRoot, zone.Width, zone.Height);
		if (paintedFloor is not null)
		{
			grid.Visible = false;
		}

		// Walls are chunked on the SAME CHUNK_TILES grid as the floor (TerrainPainter.ChunkTiles): each chunk gets
		// its own wall MultiMesh under a per-chunk Node3D ("WallChunk_<cx>_<cz>") so its small bounded AABB
		// frustum-culls independently, instead of one map-spanning MultiMesh that never culls. A blocked tile lands
		// in exactly one chunk (chunkX = tileX / CHUNK_TILES), so the union is the same wall set, just partitioned.
		// TODO(streaming): wall chunks are keyed by (cx, cz) like the floor chunks and are individually freeable,
		// so a follow-up can build/free them by player distance alongside the floor. This pass builds all of them.
		const int chunkTiles = Mmo.Client.Godot.Visuals.TerrainPainter.ChunkTiles;
		var wallChunks = new Dictionary<(int cx, int cz), List<TileCoord>>();
		foreach (var tile in zone.BlockedTiles)
		{
			if (!_renderedBlockedTiles.Add(tile))
			{
				continue;
			}

			var key = (tile.X / chunkTiles, tile.Y / chunkTiles);
			if (!wallChunks.TryGetValue(key, out var bucket))
			{
				bucket = new List<TileCoord>();
				wallChunks[key] = bucket;
			}

			bucket.Add(tile);
		}

		foreach (var ((cx, cz), wallTiles) in wallChunks)
		{
			if (wallTiles.Count == 0)
			{
				continue;
			}

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

			var wallChunk = new Node3D { Name = $"WallChunk_{cx}_{cz}" };
			wallChunk.AddChild(new MultiMeshInstance3D
			{
				Name = "WallTiles",
				Multimesh = wallMultiMesh,
				MaterialOverride = _wallMaterial
			});
			_wallRoot.AddChild(wallChunk);
		}

		// S109: hand the HUD minimap a READ-ONLY snapshot of the static map (extents + wall set) so it can bake its
		// simplified top-down raster ONCE. This is the same seed-regenerated ZoneModel the 3D world is built from
		// (read-only — no movement/world state is mutated). The Generation bumps per zone so the minimap re-bakes.
		_minimapGeneration++;
		_hudState.Map = new Mmo.Client.Godot.UI.HudState.MinimapMap(
			zone.Width, zone.Height, zone.BlockedTiles, _minimapGeneration);
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

	// COMBAT-QOL: drain this frame's damage events and float a "-N" number over each victim, then advance the rise/fade
	// of all live numbers. Each event is anchored at the victim VISUAL's current position (so the number tracks where
	// the entity is rendered, including interpolation); if the victim isn't currently rendered (out of view / not yet
	// spawned) the event is simply dropped — there is nothing to float a number over. Runs AFTER UpdateEntities so the
	// visuals' positions are this frame's. Pooling + the manager's cap keep rapid hits from leaking nodes.
	private void UpdateFloatingDamageNumbers(double delta)
	{
		if (_client is null || _floatingText is null || _renderer is null)
		{
			return;
		}

		if (_client.DrainDamageEvents(_damageEventScratch) > 0)
		{
			foreach (var damage in _damageEventScratch)
			{
				if (_renderer.TryGetActiveVisual(damage.NetworkId, out var visual))
				{
					_floatingText.Spawn(visual.Position, damage.Amount);
				}
			}
		}

		_floatingText.Update(delta);
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
		// S95: focus on a tunable blend of the confirmed tile and the cosmetic render position, temporally smoothed
		// (frame-rate independent). Defaults (blend 1.0, smoothing 0) = hard-follow the rendered character.
		// CAMERA-EXPERIMENT (F1 Movement "track predicted tile"): target the DISCRETE predicted tile instead of the
		// character render — both inputs become the predicted tile so the blend is moot. Needs smoothing > 0 to glide
		// the tile-to-tile jumps. The tracker snaps on the first frame and on teleports.
		double tileX = localState.AuthoritativeTile.X, tileY = localState.AuthoritativeTile.Y;
		double cosX = localState.Position.X, cosY = localState.Position.Y;
		if (_cameraTrackPredictedTile && _client?.PredictedLocalTile is { } predTile)
		{
			tileX = predTile.X;
			tileY = predTile.Y;
			cosX = predTile.X;
			cosY = predTile.Y;
		}
		var (focusX, focusY) = _cameraFocus.Advance(
			tileX, tileY, cosX, cosY,
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
					"WASD is screen-relative. W=up, D=right, S+D=down-right. Enter/T opens chat. F3 = perf HUD, F1 = tuning panel (admin)." +
					movementDebug);
			}
			else
			{
				// Clean default: one minimal line — who you are, connection state, and the key hints.
				SetTextIfChanged(_statusLabel,
					$"{PlayerName}  {_client.State}\n" +
					"WASD to move. Enter/T to chat. E to harvest. F3 = perf HUD, F1 = tuning panel.");
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
		UpdateLootWindow();
		UpdateInteractFeedback(now);
		// S109: RefreshHud moved OUT of here to _Process AFTER SampleMotionMetrics (read-order fix) so the minimap
		// reads the freshest local position/facing. Do not re-add it here — that reintroduces the one-frame-stale feed.
	}

	// S107: feed _hudState from already-available READ-ONLY client state, then push it to the HUD. Local
	// position/facing are real (the same render sample the camera/minimap use); vitals/cooldowns/portrait stay
	// stubbed (TODO(server)) and are varied only by the F5 debug control. Additive — reads nothing it mutates,
	// touches no movement/snapshot/prediction path. S109: now called from _Process AFTER SampleMotionMetrics
	// (every frame) so the minimap consumes the freshest local position/facing, not last frame's stale sample.
	private void RefreshHud()
	{
		if (_hud is null)
		{
			return;
		}

		// Local player position/facing — REAL, read-only (see HudState minimap fields). _hasLocalRender + the
		// cached _localRenderX/Y are computed in SampleMotionMetrics each frame; we only read them here.
		_hudState.HasLocalPosition = _hasLocalRender;
		_hudState.LocalX = _localRenderX;
		_hudState.LocalY = _localRenderY;
		_hudState.LocalFacing = _lastSentDirection;

		// COMBAT-S1: feed the 3 vitals bars from the REAL replicated stats (HP green / mana blue / stamina yellow),
		// filled proportional to current/max. Once a PlayerStatsMessage has arrived (right after login) this is
		// authoritative each frame; before that the stub/F5 values remain so the HUD still renders. Read-only.
		if (_client?.LocalStats is { } stats)
		{
			_hudState.Health = stats.Health;
			_hudState.MaxHealth = stats.MaxHealth;
			_hudState.Resource = stats.Mana;
			_hudState.MaxResource = stats.MaxMana;
			_hudState.Stamina = stats.Stamina;
			_hudState.MaxStamina = stats.MaxStamina;
		}

		// COMBAT-TUNING (radial cooldown): feed the LMB autoattack slot the REAL attack-cooldown remaining — a 0..1
		// sweep fraction (RadialCooldowns["LMB"]) + the remaining seconds for the countdown number (Cooldowns["LMB"]).
		// Both come from the client's own last-attack bookkeeping against the replicated combat.attackCooldownMs, so
		// the indicator tracks the server cadence and reacts live to a tuning tweak. This replaces the stub for the
		// LMB slot only; the other slots keep their stub/local-tick cooldowns. Written every frame (authoritative).
		if (_client is { } client)
		{
			var fraction = client.AttackCooldownRemainingFraction(out var remainingSeconds);
			_hudState.RadialCooldowns["LMB"] = (float)fraction;
			_hudState.Cooldowns["LMB"] = (float)remainingSeconds;
		}

		// COMBAT-TUNING: keep the free-aim wedge mesh matching the replicated half-angle/radius (rebuilds only on a
		// snapshot change). Cheap no-op when unchanged; ensures a live combat.halfAngleDeg/radiusTiles tweak updates
		// the telegraph without a restart even if no swing has occurred since the change.
		RebuildAimWedgeMeshIfNeeded();
		// COMBAT-TUNING: if the F8 panel is open and the server broadcast a new snapshot (after an Apply), re-seed its
		// fields to the authoritative post-clamp values.
		ReseedCombatFieldsIfChanged();
		// LIVING-ENEMIES P2-POLISH: keep the Monster tab in sync with the replicated per-type snapshot the same way.
		ReseedMonsterFieldsIfChanged();

		// S110: feed the minimap world objects (trees/rocks/resource nodes) from the SAME per-frame render-state
		// list the 3D world renders from — read-only, AOI-scoped ("current environment"). Rebuilt in place each
		// refresh (AOI-bounded count) so no new server feed is needed and no allocation churns per frame.
		RefreshMinimapObjects();

		_hud.SetState(_hudState);
	}

	// S110: project the client's known resource nodes onto HudState.MinimapObjects. Resources are point entities
	// (one tile position) in the protocol — there is no replicated collision footprint — so the on-map square side
	// is derived per-kind from a presentation constant (tree reads larger than rock; neutral default). The minimap
	// scales these by its live zoom. Read-only: touches no movement/snapshot/AOI state.
	private void RefreshMinimapObjects()
	{
		_hudState.MinimapObjects.Clear();
		for (var i = 0; i < _renderStates.Count; i++)
		{
			var state = _renderStates[i];
			if (state.Kind != EntityKind.Resource)
			{
				continue;
			}

			var footprint = MinimapFootprintTiles(state.DisplayName);
			_hudState.MinimapObjects.Add(new Mmo.Client.Godot.UI.HudState.MinimapObject(
				(float)state.Position.X, (float)state.Position.Y, footprint, state.Depleted));
		}
	}

	// Per-kind minimap footprint in world tiles. No footprint is replicated, so these mirror the relative on-screen
	// bulk of each resource model (a tree is a bigger landmark than a rock); a 2-tile object reads twice the side of
	// a 1-tile one. Presentation-only constant — tune freely without touching the protocol.
	private static float MinimapFootprintTiles(string displayName)
	{
		return displayName switch
		{
			"Tree" => 1.5f,
			"Rock" => 1.0f,
			_ => 1.0f,
		};
	}

	// S107 debug control (F5 "HUD: cycle stub states"): step the stubbed vitals/portrait through demo presets so a
	// visual check can see each HUD state live (no restart, no launch flag). Cooldown stubs are added too so the
	// action-bar slice has data to render later. Only mutates the STUB fields of _hudState — never the real
	// local-position fields, never any movement state.
	private void CycleHudStubState()
	{
		_hudStubPreset = (_hudStubPreset + 1) % 4;
		_hudState.MaxHealth = 100f;
		_hudState.MaxResource = 100f;
		_hudState.MaxStamina = 100f;
		_hudState.Cooldowns.Clear();

		// S108: also seed the action-bar stub fields so the F5 cycler visibly drives the new bar. Consumable stack
		// counts (keys "1","2") and the selected spell slot are stubbed here; the SlotButtons own the per-frame
		// cooldown countdown locally, so the seeded Cooldowns values are just the START times. TODO(server): real
		// counts come from the client item registry and selection/cooldowns from a future ability system.
		_hudState.Counts["1"] = 12;
		_hudState.Counts["2"] = 4;
		switch (_hudStubPreset)
		{
			case 0: // healthy, full resource, no cooldowns
				_hudState.Health = 100f;
				_hudState.Resource = 100f;
				_hudState.Stamina = 100f;
				_hudState.Mounted = false;
				_hudState.SelectedSlot = "R";
				break;
			case 1: // mid health/resource, several cooldowns running across slot types
				_hudState.Health = 60f;
				_hudState.Resource = 35f;
				_hudState.Stamina = 50f;
				_hudState.Mounted = false;
				_hudState.SelectedSlot = "Q";
				_hudState.Cooldowns["Q"] = 4.5f;
				_hudState.Cooldowns["R"] = 12f;
				_hudState.Cooldowns["RMB"] = 3f;
				break;
			case 2: // low health -> portrait should flip to LowHealth (red tint)
				_hudState.Health = 15f;
				_hudState.Resource = 10f;
				_hudState.Stamina = 20f;
				_hudState.Mounted = false;
				_hudState.SelectedSlot = "E";
				_hudState.Cooldowns["E"] = 6f;
				_hudState.Cooldowns["1"] = 8f;
				break;
			default: // mounted -> portrait should flip to Mount (mount badge)
				_hudState.Health = 80f;
				_hudState.Resource = 70f;
				_hudState.Stamina = 85f;
				_hudState.Mounted = true;
				_hudState.SelectedSlot = "F";
				_hudState.Cooldowns["F"] = 10f;
				break;
		}

		RefreshHud();
	}

	// S111: route the SAME owner-only inventory data into the new toggleable Inventory window (UI/InventoryWindow)
	// instead of the retired top-right text panel. The Version guard is unchanged — we only rebuild the window's
	// rows when the client inventory actually changed (gather/consume). This reads _client.Inventory and the
	// registry READ-ONLY (ToOrderedRows) and re-presents the result; it does NOT touch the inventory data path.
	private void UpdateInventory()
	{
		if (_hud is null || _client is null)
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
		_hud.SetInventory(rows, _itemRegistry);
	}

	// LOOT P4c: drive the corpse loot window from the client's replicated mirror (CorpseLootVersion-guarded). When the
	// version changes we either fill+show the window from the open corpse's rarity-tagged rows (server sent Open=true)
	// or hide it (server sent Open=false / close — emptied, decayed, out of range, despawned). Presentation only; the
	// server is authoritative for every take. Resolves display names against the registry, like the inventory window.
	private void UpdateLootWindow()
	{
		if (_hud?.Loot is not { } window || _client is null)
		{
			return;
		}

		if (_client.CorpseLootVersion == _renderedCorpseLootVersion)
		{
			return;
		}

		_renderedCorpseLootVersion = _client.CorpseLootVersion;

		if (_client.CorpseLoot is { } loot)
		{
			window.SetContents(loot.CorpseNetworkId, loot.ToRows(_itemRegistry));
		}
		else
		{
			window.HideWindow();
		}
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
			// LOOT P4b: corpse loot rejected because the interactor didn't earn the kill (not in eligibleLooters).
			"not_eligible" => "You can't loot this — you didn't earn it.",
			"no_target" => "No target.",
			"no_actor" => "No character.",
			"no_inventory" => "No inventory.",
			_ => string.IsNullOrEmpty(reason) ? "Harvest failed." : $"Harvest failed: {reason}"
		};
	}

	// F1: toggle the ADMIN tuning panel. ADMIN-ONLY — a non-admin press is a no-op (SetDebugPanelVisible
	// short-circuits when the role isn't Admin, so the panel never shows and nothing is built).
	private void ToggleDebugPanel()
	{
		if (_debugPanel is null)
		{
			return;
		}

		SetDebugPanelVisible(!_debugPanelVisible);
	}

	// F3 / client_toggle_perf: toggle the standalone perf overlay.
	private void TogglePerfPanel()
	{
		if (_perfPanel is null)
		{
			return;
		}

		SetPerfPanelVisible(!_perfPanelVisible);
	}

	// Kept for the client_toggle_perf control-channel command — now opens (or keeps open) the standalone F3 perf
	// overlay (it used to open the consolidated panel on the Perf tab; the perf surface is its own panel again).
	private void OpenDebugPanelOnPerfTab()
	{
		if (_perfPanel is null)
		{
			return;
		}

		if (!_perfPanelVisible)
		{
			SetPerfPanelVisible(true);
		}
	}

	// Show/hide the standalone perf overlay. It drives _debugOverlayVisible so the perf HUD readout, the server
	// metrics panel, and the full status-panel diagnostics follow it (as they did when perf was the non-admin
	// overlay). Forces an immediate repaint so the toggle feels instant.
	private void SetPerfPanelVisible(bool visible)
	{
		_perfPanelVisible = visible;
		_perfPanel!.Visible = visible;
		_debugOverlayVisible = visible;
		if (_metricsPanel is not null)
		{
			_metricsPanel.Visible = visible;
		}

		_nextPerfHudAt = 0;
		_nextOverlayAt = 0;
	}

	// Shared show/hide for the F1 admin tuning panel. ADMIN-ONLY: a non-admin request to open is dropped (the tabs
	// are never built and the panel never shows). Seeds on open (once-only vs. every-open per the old panels) and
	// forces an immediate overlay repaint so the toggle feels instant.
	private void SetDebugPanelVisible(bool visible)
	{
		// Admin-only panel: a non-admin gets nothing (no session → don't open, nothing to show).
		if (visible && _client?.Role != ClientRole.Admin)
		{
			return;
		}

		_debugPanelVisible = visible;
		if (visible)
		{
			// Build the tuning tabs on first Admin open (role is unknown at construction), then seed.
			EnsureAdminTabsBuilt();

			if (!_debugFieldsSeeded)
			{
				SeedDebugFieldsOnce();
				_debugFieldsSeeded = true;
			}

			// Re-seed the every-open tabs (Vitals/Combat) so they reflect the current authoritative values
			// (these change server-side, so showing the latest truth on each open is the right default).
			SeedStatFields();
			SeedCombatFields();
			SeedMonsterFields();
		}

		_debugPanel!.Visible = visible;
		_nextOverlayAt = 0;
	}

	// First-open seeding for the once-only tabs (mirrors the old F4/F5/F6 "seed on first open" behavior — re-seeding
	// would stomp values the human typed but hasn't applied). The panel is admin-only, but keep the role guard so the
	// field refs are never dereferenced before the tabs are built.
	private void SeedDebugFieldsOnce()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		SeedTuningFields();
		SeedVisualFields();
		SeedMovementFields();
	}

	// Seed the Vitals fields from the live replicated vitals (PlayerStatsMessage). Until the first stats arrive the
	// local player has no replicated vitals, so the fields are left blank.
	private void SeedStatFields()
	{
		if (_client?.LocalStats is { } stats)
		{
			SetField(_statHealthEdit, stats.Health);
			SetField(_statManaEdit, stats.Mana);
			SetField(_statStaminaEdit, stats.Stamina);
		}
	}

	// COMBAT-TUNING: seed the F8 fields from the live replicated combat snapshot. Re-seeded whenever the snapshot
	// version changes (a server broadcast after an Apply) so the panel always shows the authoritative post-clamp
	// values. Until the first snapshot arrives there is nothing to seed (fields stay blank).
	private void SeedCombatFields()
	{
		if (_client?.CombatTuning is { } tuning)
		{
			SetField(_combatAttackCooldownMs, tuning.AttackCooldownMs);
			SetField(_combatRootMs, tuning.RootMs);
			SetField(_combatHalfAngleDeg, tuning.HalfAngleDegrees);
			SetField(_combatRadiusTiles, tuning.RadiusTiles);
			SetField(_combatDamage, tuning.Damage);
			_combatPanelSeededVersion = _client.CombatTuningVersion;
		}
	}

	// COMBAT-TUNING: when the replicated snapshot changes (server broadcast after an Apply) AND the panel is open,
	// re-seed the fields so they reflect the authoritative post-clamp values. Called each RefreshHud; cheap version
	// compare. Only re-seeds while open so it never stomps un-applied edits in a closed panel.
	private void ReseedCombatFieldsIfChanged()
	{
		if (_debugPanelVisible && _client is { } client && client.CombatTuningVersion != _combatPanelSeededVersion)
		{
			SeedCombatFields();
		}
	}

	// LIVING-ENEMIES P2-POLISH: (re)populate the Monster-tab dropdown from the replicated per-type tuning and seed the
	// fields from the SELECTED type's values. Re-seeded whenever the snapshot version changes (a server broadcast after
	// an Apply) so the panel shows the authoritative post-clamp values. Until the first snapshot arrives there is
	// nothing to seed (fields stay blank, dropdown empty).
	private void SeedMonsterFields()
	{
		if (_monsterTypeDropdown is null || _client?.MonsterTuning is not { } tuning || tuning.Types.Count == 0)
		{
			return;
		}

		// Rebuild the dropdown items if the type set changed (count differs) — cheap, and the type set is tiny + rarely
		// changes. Preserve the current selection where possible.
		if (_monsterTypeDropdown.ItemCount != tuning.Types.Count)
		{
			_monsterTypeDropdown.Clear();
			for (var i = 0; i < tuning.Types.Count; i++)
			{
				_monsterTypeDropdown.AddItem(tuning.Types[i].DisplayName, i);
			}
		}

		_monsterSelectedTypeIndex = Math.Clamp(_monsterSelectedTypeIndex, 0, tuning.Types.Count - 1);
		_monsterTypeDropdown.Select(_monsterSelectedTypeIndex);

		var t = tuning.Types[_monsterSelectedTypeIndex];
		SetField(_monsterMaxHealth, t.MaxHealth);
		SetField(_monsterMoveSpeed, t.MoveSpeedMultiplier);
		SetField(_monsterRoamRadius, t.RoamRadius);
		SetField(_monsterAggroRadius, t.AggroRadius);
		SetField(_monsterChaseLeash, t.ChaseLeash);
		SetField(_monsterAttackRange, t.AttackRange);
		SetField(_monsterAttackDamage, t.AttackDamage);
		SetField(_monsterAttackCooldownMs, t.AttackCooldownMs);
		SetField(_monsterPauseMinMs, t.PauseMinMs);
		SetField(_monsterPauseMaxMs, t.PauseMaxMs);
		SetField(_monsterRespawnMs, t.RespawnMs);
		_monsterPanelSeededVersion = _client.MonsterTuningVersion;
	}

	// LIVING-ENEMIES P2-POLISH: re-seed the Monster tab when the replicated snapshot changes AND the panel is open
	// (mirrors ReseedCombatFieldsIfChanged). Only while open so it never stomps un-applied edits in a closed panel.
	private void ReseedMonsterFieldsIfChanged()
	{
		if (_debugPanelVisible && _client is { } client && client.MonsterTuningVersion != _monsterPanelSeededVersion)
		{
			SeedMonsterFields();
		}
	}

	// Seed the Server-tab fields from ServerHello (the server's startup truth). Only called once on first open
	// (re-seeding would stomp values the human has typed but not yet applied).
	private void SeedTuningFields()
	{
		// SPEED1: only aoi.interestRadius remains — the base step cooldown is a pinned constant, no longer tunable.
		var serverRadius = _client?.Server?.InterestRadiusTiles ?? 35f;
		SetField(_tuneInterestRadius, serverRadius);
	}

	// Seed the Visual-tab client-local fields from the live local state/_tuning. Only called once on first open.
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

	// S102: seed the Movement-tab client-local movement/feel fields from the live local values. Only called once on
	// first open (re-seeding would stomp un-applied edits), mirroring SeedVisualFields.
	private void SeedMovementFields()
	{
		// Seed from the live values so re-opening shows the current state.
		SetField(_moveNetLatencyMs, _client?.SimulatedLatencyMs ?? 0);
		SetField(_moveCameraFollowBlend, _cameraFollowBlend);
		SetField(_moveCameraSmoothing, _cameraSmoothing);
		// New (S102).
		SetField(_moveCameraTeleportSnapTiles, _cameraTeleportSnapTiles);
		// S106: build the "Move speed" dropdown items from ServerHello (base cadence + tick rate) and preselect the
		// default walk. Done here (first open) since ServerHello has landed by login.
		PopulateMoveSpeedDropdown();
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

	// Server-tab apply-all: parse every SERVER field and send it via AdminSetTuning (the server admin-gates + clamps
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

	// Vitals-tab apply (COMBAT-S1): parse each vitals field and send its CURRENT value to the server via AdminSetStat (stat
	// byte 0=HP, 1=mana, 2=stamina; the server admin-gates + clamps to [0,max] and replicates the result back so
	// the bars track it). Invalid/blank fields are skipped so a typo in one never blocks the others.
	private void OnStatApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		if (TryReadField(_statHealthEdit, out var hp))
		{
			_client.SendAdminSetStat((byte)StatKind.Health, (int)Math.Round(hp));
		}

		if (TryReadField(_statManaEdit, out var mana))
		{
			_client.SendAdminSetStat((byte)StatKind.Mana, (int)Math.Round(mana));
		}

		if (TryReadField(_statStaminaEdit, out var stamina))
		{
			_client.SendAdminSetStat((byte)StatKind.Stamina, (int)Math.Round(stamina));
		}

		ShowInteractFeedback("Vitals sent.");
	}

	// Combat-tab apply-all (COMBAT-TUNING): parse each combat field and send it via AdminSetTuning on its combat.* registry key.
	// The server admin-gates + clamps each authoritatively, then broadcasts the replicated CombatTuningSnapshot back —
	// which re-seeds this panel (post-clamp values) and rebuilds the wedge/predictor/cooldown viz live. Invalid/blank
	// fields are skipped so a typo in one never blocks the others.
	private void OnCombatApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		if (TryReadField(_combatAttackCooldownMs, out var cooldownMs))
		{
			_client.SendAdminSetTuning("combat.attackCooldownMs", cooldownMs);
		}

		if (TryReadField(_combatRootMs, out var rootMs))
		{
			_client.SendAdminSetTuning("combat.rootMs", rootMs);
		}

		if (TryReadField(_combatHalfAngleDeg, out var halfAngle))
		{
			_client.SendAdminSetTuning("combat.halfAngleDeg", halfAngle);
		}

		if (TryReadField(_combatRadiusTiles, out var radius))
		{
			_client.SendAdminSetTuning("combat.radiusTiles", radius);
		}

		if (TryReadField(_combatDamage, out var damage))
		{
			_client.SendAdminSetTuning("combat.damage", damage);
		}

		ShowInteractFeedback("Combat tuning sent.");
	}

	// LIVING-ENEMIES P2-POLISH: a monster type was picked in the dropdown — remember the index and re-seed the fields
	// from THAT type's replicated values. (Admin-only panel; the dropdown is only built for an admin.)
	private void OnMonsterTypeSelected(long index)
	{
		_monsterSelectedTypeIndex = (int)index;
		SeedMonsterFields();
	}

	// Monster-tab apply-all: parse each field and send it via AdminSetTuning on the SELECTED type's "<typeId>.<field>"
	// key (e.g. slime.roamRadius). The server admin-gates + clamps each authoritatively, then broadcasts the replicated
	// MonsterTuningSnapshot back — which re-seeds this panel (post-clamp values). Invalid/blank fields are skipped so a
	// typo in one never blocks the others. No-op if there is no replicated type to target yet.
	private void OnMonsterApplyPressed()
	{
		if (_client?.Role != ClientRole.Admin || !TryGetSelectedMonsterTypeId(out var typeId))
		{
			return;
		}

		SendMonsterField(typeId, "maxHealth", _monsterMaxHealth);
		SendMonsterField(typeId, "moveSpeed", _monsterMoveSpeed);
		SendMonsterField(typeId, "roamRadius", _monsterRoamRadius);
		SendMonsterField(typeId, "aggroRadius", _monsterAggroRadius);
		SendMonsterField(typeId, "chaseLeash", _monsterChaseLeash);
		SendMonsterField(typeId, "attackRange", _monsterAttackRange);
		SendMonsterField(typeId, "attackDamage", _monsterAttackDamage);
		SendMonsterField(typeId, "attackCooldownMs", _monsterAttackCooldownMs);
		SendMonsterField(typeId, "pauseMinMs", _monsterPauseMinMs);
		SendMonsterField(typeId, "pauseMaxMs", _monsterPauseMaxMs);
		SendMonsterField(typeId, "respawnMs", _monsterRespawnMs);

		ShowInteractFeedback($"Monster tuning sent ({typeId}).");
	}

	// Sends one per-type field via AdminSetTuning on "<typeId>.<field>" iff the field parses. Skips a blank/invalid one.
	private void SendMonsterField(string typeId, string field, LineEdit? edit)
	{
		if (TryReadField(edit, out var value))
		{
			_client?.SendAdminSetTuning($"{typeId}.{field}", value);
		}
	}

	// Resolves the dropdown selection to a replicated type id. False if no replicated tuning / no types yet.
	private bool TryGetSelectedMonsterTypeId(out string typeId)
	{
		typeId = "";
		if (_client?.MonsterTuning is not { } tuning || tuning.Types.Count == 0)
		{
			return false;
		}

		var index = Math.Clamp(_monsterSelectedTypeIndex, 0, tuning.Types.Count - 1);
		typeId = tuning.Types[index].Id;
		return true;
	}

	// Visual-tab apply-all (S65): parse every CLIENT-LOCAL VISUAL field and apply it INSTANTLY in place (no server
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

	// Movement-tab apply-all (S102): parse every CLIENT-LOCAL MOVEMENT/FEEL field and apply it INSTANTLY in place (no server
	// round-trip, no restart). Net latency routes to the client; camera blend/smoothing/teleport-snap are local
	// _camera* fields the next UpdateCamera reads. The stop-on-reversal toggle applies live on click (not here).
	// Invalid fields are skipped so a typo in one never blocks the others.
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
		// The wire-facing name stays TogglePerfHud (debug-control protocol / client_toggle_perf); it now flips the
		// standalone F3 perf overlay (the perf surface is its own panel again, not a tab on the F1 tuning panel).
		TogglePerfPanel();
	}

	void IControlHost.ToggleFullscreen()
	{
		var mode = DisplayServer.WindowGetMode();
		DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
			? DisplayServer.WindowMode.Windowed
			: DisplayServer.WindowMode.Fullscreen);
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
			_frameCsv.WriteLine("elapsedSec,frameMs,pollMs,renderStateMs,entitiesMs,cameraMs,overlayMs,gc0,gc1,gc2,localRenderX,localRenderY,confirmedX,confirmedY,divergence,frameDelta,predX,predY,stepSeq,recMatched,recCorrected,recSnapped,cadenceMs");
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

		// RENDER-VELOCITY DIAG: the local predictor's internals this frame so a velocity jump (renderX/frameDelta)
		// can be pinned to its trigger — a predicted-tile RE-PROJECTION (predX/predY jump >1 in a frame) or a
		// reconcile CATCH-UP (recCorrected/recSnapped tick up). Counts are cumulative; analysis reads the deltas.
		// Blank when no predictor is attached (pre-spawn / interpolation-only).
		string predX = string.Empty, predY = string.Empty, stepSeq = string.Empty,
			recMatched = string.Empty, recCorrected = string.Empty, recSnapped = string.Empty, cadenceMs = string.Empty;
		if (_client?.LocalPredictorFrameDiagnostics is { } diag)
		{
			predX = diag.PredictedX.ToString(CultureInfo.InvariantCulture);
			predY = diag.PredictedY.ToString(CultureInfo.InvariantCulture);
			stepSeq = diag.PredictedStepSeq.ToString(CultureInfo.InvariantCulture);
			recMatched = diag.ReconcileMatched.ToString(CultureInfo.InvariantCulture);
			recCorrected = diag.ReconcileCorrected.ToString(CultureInfo.InvariantCulture);
			recSnapped = diag.ReconcileSnapped.ToString(CultureInfo.InvariantCulture);
			cadenceMs = diag.CadenceMs.ToString("0.###", CultureInfo.InvariantCulture);
		}

		var row = string.Create(CultureInfo.InvariantCulture,
			$"{_elapsedSeconds:0.###},{_lastFrameMs:0.###},{_lastPollMs:0.###},{_lastRenderStateMs:0.###},{_lastEntitiesMs:0.###},{_lastCameraMs:0.###},{_lastOverlayMs:0.###},{dGc0},{dGc1},{dGc2},{renderX},{renderY},{confX},{confY},{divergence},{frameDelta},{predX},{predY},{stepSeq},{recMatched},{recCorrected},{recSnapped},{cadenceMs}");
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

	// LIVING-ENEMIES P2-POLISH: the shared flat quad + RED material for the monster-home markers. Full-tile (0.96) so
	// a home reads as "this tile", red + semi-transparent + unshaded so it sits flat and legible over any terrain.
	private static readonly PlaneMesh MonsterHomeMarkerMesh = new() { Size = new Vector2(0.96f, 0.96f) };
	private static readonly StandardMaterial3D MonsterHomeMarkerMaterial = MarkerMaterial(new Color(0.90f, 0.12f, 0.12f, 0.55f));

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

	// LIVING-ENEMIES P2-POLISH: sync the RED monster-home markers to the client's known monster homes each frame. A
	// marker is created when a new home appears (a monster entered AOI) and freed when its home is gone (the monster
	// despawned / left AOI, dropping the home client-side). Cheap: the set is tiny (a handful of monsters) and only
	// diffs against the live MonsterHomes dictionary — no per-frame allocation in the steady state. Always on (no
	// toggle) — the leash anchor is gameplay-legibility, not a debug overlay. No-op until the world root exists.
	private void UpdateMonsterHomeMarkers()
	{
		if (_worldRoot is null || _client is null)
		{
			return;
		}

		// F1 Visual "Spawner tiles" toggle (default OFF): a debug visualizer like the prediction-tiles markers.
		// When off, free any shown markers and skip — the red spawner anchors only appear when the toggle is on.
		if (!_showSpawnerTiles)
		{
			if (_monsterHomeMarkers.Count > 0)
			{
				foreach (var marker in _monsterHomeMarkers.Values)
				{
					marker.QueueFree();
				}

				_monsterHomeMarkers.Clear();
			}

			return;
		}

		// LIVING-ENEMIES P3: the red anchors are now keyed by persistent SPAWNER id (not monster network id), so they
		// stay put across a monster's death/respawn. Source is SpawnerMarkers (added/dropped by SpawnerMarker messages).
		var spawners = _client.SpawnerMarkers;

		// Drop markers whose spawner is gone (left AOI / removed).
		if (_monsterHomeMarkers.Count > 0)
		{
			_monsterHomeStaleScratch.Clear();
			foreach (var id in _monsterHomeMarkers.Keys)
			{
				if (!spawners.ContainsKey(id))
				{
					_monsterHomeStaleScratch.Add(id);
				}
			}

			foreach (var id in _monsterHomeStaleScratch)
			{
				_monsterHomeMarkers[id].QueueFree();
				_monsterHomeMarkers.Remove(id);
			}
		}

		// Add/position a marker per known spawner (the spawner tile is fixed, so positioning once on create suffices —
		// re-setting it is a cheap idempotent assignment).
		foreach (var (spawnerId, spawnerTile) in spawners)
		{
			if (!_monsterHomeMarkers.TryGetValue(spawnerId, out var marker))
			{
				marker = new MeshInstance3D
				{
					Name = $"Spawner_{spawnerId}",
					Mesh = MonsterHomeMarkerMesh,
					MaterialOverride = MonsterHomeMarkerMaterial,
				};
				_worldRoot.AddChild(marker);
				_monsterHomeMarkers[spawnerId] = marker;
			}

			// A hair above the ground (below the prediction markers' 0.04 so those still win any overlap z-fight).
			marker.Position = TileToWorld(spawnerTile, 0.03f);
		}
	}

	// FREEAIM FEEL KNOBS (client telegraph). COMBAT-TUNING: the half-angle/radius the wedge is drawn from are no
	// longer client constants — they MIRROR the server's REPLICATED CombatTuningSnapshot (combat.halfAngleDeg /
	// combat.radiusTiles), so the drawn wedge ALWAYS equals the server's real danger area (the earlier "keep these in
	// sync by hand" duplication is gone). The mesh is rebuilt whenever the replicated snapshot changes
	// (RebuildAimWedgeMeshIfNeeded, keyed off MmoClient.CombatTuningVersion). These defaults reproduce the historical
	// look before the first snapshot lands (45°, 1.6 tiles).
	private float _aimWedgeHalfAngleDegrees = 45f;
	private float _aimWedgeRadiusTiles = 1.6f;
	private int _aimWedgeBuiltTuningVersion = -1;
	private ArrayMesh? _aimWedgeMesh;

	// FREEAIM: red ground material for the aim wedge — unshaded + alpha so it sits flat over any terrain.
	private static readonly StandardMaterial3D AimWedgeMaterial = MarkerMaterial(new Color(0.95f, 0.15f, 0.15f, 0.45f));

	// How long the wedge stays lit after an attack (ms). A fixed ~250 ms telegraph beat — DECOUPLED from the
	// movement root (the root default is now 0 = no lock, so the wedge can't borrow that duration anymore). Well
	// inside the ~600 ms attack cooldown, so each swing's flash clears before the next.
	private const ulong AimWedgeFlashMs = 250;

	// COMBAT-TUNING: (re)build the flat wedge (pie-slice) mesh from the CURRENT half-angle/radius, authored in the XZ
	// plane pointing along +X, spanning [-half, +half] out to radius. A triangle fan from the apex (player origin);
	// the MeshInstance3D is yawed by -aimRadians so +X maps to the world aim bearing. Cheap (32 verts) and only on a
	// tuning change, so it never runs on the hot path.
	private ArrayMesh BuildAimWedgeMesh(float halfAngleDegrees, float radiusTiles)
	{
		const int segments = 16;
		var half = Mathf.DegToRad(halfAngleDegrees);
		var verts = new Godot.Collections.Array();
		verts.Resize((int)Mesh.ArrayType.Max);

		var points = new System.Collections.Generic.List<Vector3>(segments + 2)
		{
			Vector3.Zero // apex at the player origin
		};
		for (var i = 0; i <= segments; i++)
		{
			var a = -half + (2f * half * i / segments);
			// Author in XZ pointing along +X: (cos a, 0, sin a) * radius. Yawing by -aim later rotates +X to the aim.
			points.Add(new Vector3(Mathf.Cos(a) * radiusTiles, 0f, Mathf.Sin(a) * radiusTiles));
		}

		var vertexArray = new Vector3[segments * 3];
		var v = 0;
		for (var i = 1; i <= segments; i++)
		{
			// Wind so the triangle faces up (+Y); the material is double-sided (CullMode.Disabled) anyway.
			vertexArray[v++] = points[0];
			vertexArray[v++] = points[i + 1];
			vertexArray[v++] = points[i];
		}

		verts[(int)Mesh.ArrayType.Vertex] = vertexArray;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, verts);
		return mesh;
	}

	// COMBAT-TUNING: adopt the replicated half-angle/radius and rebuild the wedge mesh if the snapshot changed since
	// the last build (or on first build). Called before a flash and from RefreshHud so a live combat.* tweak updates
	// the telegraph without a restart. No-op when the version is unchanged.
	private void RebuildAimWedgeMeshIfNeeded()
	{
		var version = _client?.CombatTuningVersion ?? 0;
		if (_aimWedgeMesh is not null && version == _aimWedgeBuiltTuningVersion)
		{
			return;
		}

		if (_client?.CombatTuning is { } tuning)
		{
			_aimWedgeHalfAngleDegrees = (float)tuning.HalfAngleDegrees;
			_aimWedgeRadiusTiles = (float)tuning.RadiusTiles;
		}

		_aimWedgeMesh = BuildAimWedgeMesh(_aimWedgeHalfAngleDegrees, _aimWedgeRadiusTiles);
		_aimWedgeBuiltTuningVersion = version;
		if (_aimWedge is not null)
		{
			_aimWedge.Mesh = _aimWedgeMesh;
		}
	}

	// Creates the wedge MeshInstance3D under the world root on first use (idempotent, hidden). No-op pre-zone.
	private void EnsureAimWedge()
	{
		if (_worldRoot is null || _aimWedge is not null)
		{
			return;
		}

		RebuildAimWedgeMeshIfNeeded();
		_aimWedge = new MeshInstance3D
		{
			Name = "AimWedge",
			Mesh = _aimWedgeMesh,
			MaterialOverride = AimWedgeMaterial,
			Visible = false
		};
		_worldRoot.AddChild(_aimWedge);
	}

	// Flashes the wedge from the local player `origin` (world XZ) oriented along `aimRadians` for AimWedgeFlashMs.
	// Called from TryAttack with the SAME aim the server resolves the sector with, so the player sees the danger area.
	private void FlashAimWedge(Vector3 origin, float aimRadians)
	{
		EnsureAimWedge();
		RebuildAimWedgeMeshIfNeeded();
		if (_aimWedge is null)
		{
			return;
		}

		// Just above the ground (matches the prediction markers) so it reads clearly over terrain.
		_aimWedge.Position = new Vector3(origin.X, 0.05f, origin.Z);
		// Yaw +X -> world aim. A +Y rotation by θ maps +X=(1,0,0) to (cosθ,0,-sinθ); the aim direction is
		// (cos aim, 0, sin aim), so θ = -aim.
		_aimWedge.Rotation = new Vector3(0f, -aimRadians, 0f);
		_aimWedge.Visible = true;
		_aimWedgeHideAtMs = Time.GetTicksMsec() + AimWedgeFlashMs;
	}

	// Per-frame: keep the lit wedge attached to the local player, then hide it once its flash window elapses.
	private void UpdateAimWedge()
	{
		if (_aimWedge is null || _aimWedgeHideAtMs == 0)
		{
			return;
		}

		if (Time.GetTicksMsec() >= _aimWedgeHideAtMs)
		{
			_aimWedge.Visible = false;
			_aimWedgeHideAtMs = 0;
			return;
		}

		// SWING-COMMIT: track the local player while the wedge is lit, so a residual in-flight tile step doesn't
		// leave the telegraph behind the avatar. The aim (the node's Y rotation, set in FlashAimWedge) stays fixed
		// at the swing direction — only the origin follows the player.
		if (TryGetLocalRenderPosition(out var px, out var pz))
		{
			_aimWedge.Position = new Vector3(px, 0.05f, pz);
		}
	}

	// FREEAIM: continuous local facing. Render-only, local-only, NOT replicated — the local player's visual yaws
	// smoothly toward the cursor's ground point each frame so the avatar "looks where you aim". The server still
	// only knows the discrete movement facing; this is pure presentation layered over it. No-op when the cursor pick
	// fails or the local visual isn't spawned yet.
	private void UpdateLocalContinuousFacing()
	{
		if (_renderer is null || _client?.LocalNetworkId is not uint localId)
		{
			return;
		}

		if (!_renderer.TryGetActiveVisual(localId, out var visual))
		{
			return;
		}

		if (!TryGetAimToCursor(out var aimRadians))
		{
			// No aim this frame: drop the override so the visual falls back to its discrete movement facing.
			visual.ClearContinuousYaw();
			return;
		}

		// Model forward is -Z; a yaw θ about +Y turns -Z into (-sinθ,0,-cosθ). The aim direction is
		// (cos aim,0,sin aim), so θ = atan2(-cos aim, -sin aim) = atan2(-dx,-dz) with the aim's unit components.
		var dx = Mathf.Cos(aimRadians);
		var dz = Mathf.Sin(aimRadians);
		visual.SetContinuousYaw(Mathf.Atan2(-dx, -dz));
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
