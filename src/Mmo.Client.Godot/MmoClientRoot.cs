using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Client.Godot.Visuals;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;

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
	// AOI-EDGE ZOOM CLAMP (user, 2026-07-02 — companion to the server AOI 30→18 default): cap the zoom-OUT so the
	// screen never shows the interest-radius edge (entities popping in/out of AOI). Derived, not hard-coded: from
	// the ServerHello-replicated InterestRadiusUnits, the fixed camera rig pitch, and the live viewport aspect —
	// the worst-case SCREEN-CORNER ground distance is kept ≥ this margin inside the radius (margin covers the
	// entity mesh + the spawn-at-exactly-r case). Re-derived if the hello radius changes; an admin can still widen
	// the clamp live via the F1 zoom-range knobs (deliberate override).
	private const float AoiEdgeHideMarginUnits = 2f;
	// MAX ZOOM-OUT FLOOR (user, 2026-07-03): the AOI-derived clamp at the 18u interest radius lands at ~13.5 —
	// too tight in play. The user set the max zoom-out to 15: the derived clamp can allow MORE (if the AOI radius
	// ever grows) but never LESS than this, trading a ~1.5u worst-case screen-corner AOI-edge peek (entity pop-in)
	// for the wider view. The F1 zoom-range knobs still override live.
	private const float UserZoomOutFloor = 15f;
	private float _appliedAoiZoomClampRadius = -1f;
	// The fixed camera rig offset (also the pitch source for the AOI zoom clamp) — one definition for both the
	// per-frame follow and the clamp math.
	private static readonly Vector3 CameraRigOffset = new(24, 28, 24);
	// S95: camera temporal smoothing (S102: live F1 lever). The tracker frame-rate-independently smooths a persistent
	// focus toward the rendered (continuous) character position, snapping on the first frame and on teleports
	// (> _cameraTeleportSnapTiles). The old "follow blend toward the confirmed TILE" knob was a tile-era remnant and
	// has been removed — in continuous movement the camera always follows the continuous render position.
	// CONTINUOUS: smoothing 10 (the good-feeling exp/continuous-movement value). The old LOW 3 was a TILE-era value:
	// it tamed the old tile-camera's jittery exponential focus-chase (the "accelerate to catch up" stutter), but the
	// continuous predictor renders SMOOTHLY, so there is no per-frame jitter left to damp — a low 3 just makes the
	// camera TRAIL the avatar and COAST after a stop. 0 = hard-follow; 10 tracks tightly with only a cosmetic glide.
	// NOTE: at a FIXED rate the lag = move-speed / rate, so faster speeds still trail a touch more — auto-scaling the
	// rate with speed would hold the lag constant. Live-tunable via the F1 Movement tab.
	private float _cameraSmoothing = 10f;
	// S95 default 4 tiles. S102: now a live F6 field (was a const) feeding CameraFocusTracker.Advance's
	// teleport-snap threshold — beyond this jump the camera hard-snaps (respawn/zone change) instead of gliding.
	private float _cameraTeleportSnapTiles = 4f;
	private CameraFocusTracker _cameraFocus;
	private double _lastFrameDelta;
	private CheckBox? _uncapFpsCheck;
	private bool _fpsUncapped;
	// PLAYER-COLLISION-TOGGLE: the F1 Server-tab live toggle for server-authoritative player↔player collision. Flips on
	// click (sends an admin message; the server broadcasts the new flag back). Re-seeded to the replicated value on each
	// panel open via SetPressedNoSignal (so seeding never fires the toggle handler → never sends a spurious admin flip).
	private CheckBox? _playerCollisionCheck;
	private CheckBox? _frameCsvCheck;
	// N-movement-trace-live-toggle: the F3 perf-panel live toggle for the console MOVE-trace (mmo_trace lines).
	// MMO_DEBUG_MOVEMENT only seeds the initial state (read inside MmoClient's trace); the checkbox is re-seeded
	// from the client right after connect and flips MmoClient.DebugMovementEnabled live — no restart.
	private CheckBox? _movementTraceCheck;
	// F1 Visual "Spawner tiles" toggle — default OFF. Debug viz of the monster spawner anchors (red tiles), gated
	// exactly like the prediction-tiles markers. Flipped by ApplySpawnerTiles; read by UpdateMonsterHomeMarkers.
	private bool _showSpawnerTiles;
	// F1 Visual "Server positions" toggle — default OFF. Debug viz of every entity's AUTHORITATIVE server position
	// (the continuous confirmed AuthoritativePosition), as distinct from its rendered body — so the human can SEE the
	// gap: for a REMOTE entity the interpolation lag (a marker leading a moving slime); for the LOCAL player the
	// prediction-vs-server gap (the confirmed position vs the predicted body). Flipped by ApplyServerPositions; read
	// by UpdateServerPositionMarkers.
	private bool _showServerPositions;
	// LIVING-ENEMIES P3: one flat RED ground marker per known SPAWNER (the persistent leash/de-aggro anchor), keyed by
	// the stable spawner id, parented under _worldRoot. Synced each _Process frame from MmoClient.SpawnerMarkers: a
	// marker is created when a spawner enters AOI and freed when it leaves. Because it tracks the SPAWNER (not the
	// monster), the red tile stays put across the monster's death/respawn — the de-aggro anchor stays legible.
	private readonly System.Collections.Generic.Dictionary<uint, MeshInstance3D> _monsterHomeMarkers = new();
	private readonly System.Collections.Generic.List<uint> _monsterHomeStaleScratch = new();

	// DEBUG-SERVER-POSITIONS: one flat CYAN ground marker per REMOTE entity (keyed by network id), parented under
	// _worldRoot, painted at that entity's continuous AuthoritativePosition (its confirmed server position). Synced
	// each _Process frame from _renderStates while the F1 "Server positions" toggle is on: a marker is created when a
	// remote entity is first seen, repositioned every frame (so it tracks the server position and visibly LEADS the
	// interpolated body under movement), and freed when the entity despawns / leaves AOI or the toggle is off. The
	// LOCAL player IS included: its body renders the PREDICTED position, so its marker shows the prediction-vs-server gap.
	private readonly System.Collections.Generic.Dictionary<uint, MeshInstance3D> _serverPositionMarkers = new();
	private readonly System.Collections.Generic.List<uint> _serverPositionStaleScratch = new();
	private readonly System.Collections.Generic.HashSet<uint> _serverPositionSeenScratch = new();

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
	// Speed has two free-multiplier fields: "My speed" sets the local player's per-entity SpeedMultiplier via /speed;
	// "Global speed" scales continuous.baseMoveSpeed (base cadence, SPEED1) live for everyone incl. bots.
	// Moved from the visual surface: net latency (S93), camera smoothing (S95). (Camera follow-blend removed — tile remnant.)
	private LineEdit? _moveNetLatencyMs;
	private LineEdit? _moveCameraSmoothing;
	// New (S102): camera teleport-snap distance (tiles) — exposes the former CameraTeleportSnapTiles const live.
	private LineEdit? _moveCameraTeleportSnapTiles;
	// remote-interp-tighten Part A: the REMOTE jitter-buffer (ms) live knob. Dials remote-entity render lag-vs-
	// smoothness in-client (no restart) — lower = the slime renders tighter to its server tile; raise to absorb more
	// arrival jitter. Empty/blank or < 0 reverts to the computed default. Applies to all remote interpolators live.
	private LineEdit? _moveRemoteInterpBufferMs;
	// GLOBAL speed as a single free MULTIPLIER (replaced the S106 discrete-bracket dropdown). Applied on Apply/Enter,
	// it scales the GLOBAL base move speed live: continuous.baseMoveSpeed = (1000 / ServerHello.StepCooldownMs) ×
	// multiplier — so the local player AND the synthetic bots (and every other entity) all move at base × multiplier.
	// Seeded to 1.0 (= default base) on first open.
	private LineEdit? _moveSpeedMultiplier;
	// PER-PLAYER speed as a free MULTIPLIER (replaced the S106 dropdown's per-entity send). Applied on Apply/Enter, it
	// sets ONLY the local player's per-entity SpeedMultiplier by sending the `/speed <multiplier>` server command (the
	// same path the removed dropdown used). Multiplies on top of the global base: effective local speed = base ×
	// globalMult × playerMult. Seeded to 1.0 on first open. Admin-gated server-side (and client-side).
	private LineEdit? _movePlayerSpeedMultiplier;

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

	// LIVING-ENEMIES P2-POLISH (DATA-DRIVEN at v40): the F1 "Monster" tab — a per-TYPE dropdown + the selected type's
	// tuning fields, now built DYNAMICALLY from the replicated MonsterTuningField list (one labelled row per field) so
	// exposing a new server-side knob needs NO client edit. Edits THAT type's live values via AdminSetTuning on
	// "<typeId>.<Key>" keys; the server clamps + broadcasts the MonsterTuningSnapshot back, which re-seeds these rows
	// (mirroring the Combat tab pattern). `_monsterFieldRows` is the container the rows live in; `_monsterFieldEdits`
	// pairs each replicated field Key with its LineEdit (rebuilt only when the selected type's field set changes).
	private int _monsterPanelSeededVersion = -1;
	private int _monsterSelectedTypeIndex;
	private OptionButton? _monsterTypeDropdown;
	private VBoxContainer? _monsterFieldRows;
	private readonly List<(string Key, LineEdit Edit)> _monsterFieldEdits = new();

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

	// NODE-FIELD N3 (docs/node-field-design.md D6): the field's render view (chunked MultiMeshes) + the
	// chunk index/jittered placements it was built from, kept so a later NodeFieldVersion change can rebuild
	// just the affected chunk(s) instead of the whole field (NodeFieldView.SyncDepletion). Null until BuildZone
	// builds them (a non-authored/genVersion-1 zone's empty catalogue leaves this null — no field to render,
	// mirroring N2's "no catalogue -> no field" behaviour). _lastSyncedNodeFieldVersion tracks the last
	// MmoClient.NodeFieldVersion we synced against so the per-frame check is a single int compare in the
	// common (nothing changed) case.
	private NodeFieldView? _nodeFieldView;
	private NodeFieldChunkIndex? _nodeFieldChunkIndex;
	private IReadOnlyList<NodeFieldPlacer.PlacedNode>? _nodeFieldPlacements;
	private int _lastSyncedNodeFieldVersion = -1;
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

	// CONTINUOUS MIGRATION (Phase 3, v36): movement input is now one per-input continuous MoveIntent PER RENDER FRAME
	// (raw direction + the frame's dt) — self-redundant (the next frame supersedes a dropped one), so the v35 fixed-
	// rate / stop-tail scheduling is gone. _lastSentMoving/_lastSentDirection still track the most recent intent for
	// the HUD facing + the mouse-heading "last heading" feed.

	// S64 mouse-heading feel constants. Dead-zone: ~0.6 tile (between the S64 0.5–0.75 guidance) — inside it the
	// held octant is kept so the heading doesn't whip when the cursor sits on/near the player. Hysteresis: 6° of
	// octant stickiness past the boundary before switching, killing flicker between two adjacent octants.
	private const double MouseHeadingDeadZoneUnits = 0.6;
	private const double MouseHeadingHysteresisDegrees = 6.0;
	private bool _lastSentMoving;
	private Direction8 _lastSentDirection;

	// FREE-ANGLE A/B TEST (client-local, NOT server-authoritative — no message/replication): when TRUE the MOUSE
	// hold-to-walk path sends the RAW player->cursor unit heading (any angle) instead of the nearest of 8 octants,
	// so the avatar follows the cursor exactly. Default FALSE = the current 8-direction behaviour. WASD is
	// inherently 8-way (8 sign combos) and is IDENTICAL in both modes; only the mouse heading changes. The predictor
	// consumes the same unit vector the client sends, so prediction stays in parity either way. Flipped live via the
	// F1 Movement-tab checkbox (no restart, no round-trip). Facing stays an 8-way sprite (nearest Direction8) in both.
	private bool _freeAngleMovement;

	// CONTROLLER (2026-07-04): Xbox/XInput support — Godot 4 handles XInput + hot-plug natively on Windows, so
	// this is "read the first connected joypad" with no manual device-arrival wiring. One radial deadzone shared
	// by both sticks (movement + aim): compared on VECTOR LENGTH, not per-axis, so it stays circular instead of
	// a smaller effective square. `_controllerAimDirection`/`_aimSourceIsController` back the single aim-source
	// seam (TryGetAimWorldPoint, near TryGetAimToCursor) that every cursor-aim call site already funnels through;
	// mouse motion reclaims aim in _UnhandledInput, the right stick reclaims it in PollControllerAim (_Process).
	// `_controllerLeftTriggerHeld`/`_prevRightTriggerHeld` back the per-frame trigger poll (PollControllerTriggers,
	// near UpdateSkillshotAim) — LT/RT are AXES on XInput pads, not buttons, so they can't arrive as
	// InputEventJoypadButton and must be polled like the sticks.
	private const float ControllerStickDeadzone = 0.25f;
	private const float ControllerTriggerThreshold = 0.5f;
	private const float ControllerAimProjectDistance = 8f; // world units; see TryGetAimWorldPoint
	private WorldVector _controllerAimDirection = new(1d, 0d); // default east; inert until _aimSourceIsController
	private bool _aimSourceIsController;
	private bool _controllerLeftTriggerHeld;
	private bool _prevRightTriggerHeld;
	// CONTROLLER AIM-FACING (2026-07-05 feel-test): true only while the right stick is past its deadzone THIS
	// frame (refreshed every PollControllerAim) — the "actively aiming" predicate for the local facing override,
	// deliberately distinct from the persistent _aimSourceIsController (which stays true long after the stick
	// recentres and would pin the facing to a stale aim while walking).
	private bool _controllerAimStickActive;

	// S56: mouse control is hold-to-walk-toward-cursor (UO), not click-a-destination. While the RIGHT mouse
	// button is held, each frame we ray the cursor to the ground plane and hold the MoveIntent heading from
	// the PREDICTED local tile toward the cursor tile (CursorHeading) — exactly the keyboard path, re-aimed
	// live. WASD takes priority while a key is down. The S53 click-a-destination DRIVE PATH is retired (its
	// ClickMoveController/TilePathfinder/PathDriver scaffold was removed); a future "click once to path there"
	// mode would re-introduce A* pathing if wanted.

	// Autopilot: a scripted movement loop that also streams per-frame telemetry to .run/client-frames.csv.
	private Direction8[]? _autopilotPattern;
	private double _autopilotEndsAtSeconds;
	private double _autopilotLegSeconds;
	private int _autopilotLegIndex;

	// Render trace (agent smoothness read): when armed via the control channel, capture a target entity's per-frame
	// render + authoritative position; on completion compute jitter/jerk/reversal/lag metrics. Recorded in _Process
	// right after SampleRenderStates. Lets an agent judge a bot's ON-SCREEN smoothness in one call — the ~1 Hz
	// entities poll can only catch coarse snapshots, not per-frame micro-jitter.
	private bool _renderTraceActive;
	private uint _renderTraceNetworkId;
	private double _renderTraceEndsAtSeconds;
	private readonly List<(double T, double Rx, double Ry, double Ax, double Ay)> _renderTraceSamples = new(256);
	private RenderTraceStatus? _renderTraceResult;
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
	private const double MotionSnapJumpUnits = 0.5;
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
	// Login name. MMO_PLAYER_NAME picks it (the launch script sets GodotA/GodotB, which the server marks admin). When
	// that env var is UNSET: in the Godot EDITOR (F5 play) default to "Admin" — an admin name — so the F1 admin panel
	// works during lookdev/dev; in a standalone build, a random name. The launch's env var still wins for GodotA/GodotB.
	[Export] public string PlayerName { get; set; } = ReadString(
		"MMO_PLAYER_NAME",
		OS.HasFeature("editor") ? "Admin" : $"Godot{Random.Shared.Next(1000, 9999)}");
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
		// Seed the F3 "Movement trace (console)" checkbox to the client's initial trace state (MMO_DEBUG_MOVEMENT
		// is only the seed; the checkbox is the live control). SetPressedNoSignal so seeding never re-toggles.
		_movementTraceCheck?.SetPressedNoSignal(_client.DebugMovementEnabled);
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

		// CONTROLLER (2026-07-04): poll aim (right stick) BEFORE movement/triggers so RT's edge-triggered
		// TryAttack() below (PollControllerTriggers) reads this frame's fresh aim, and left-stick movement
		// (SendHeldMovement, via CurrentControllerMoveHeading) sees this frame's fresh stick state too. Both are
		// ungated by chat focus / login — see each method's own comment.
		PollControllerAim();
		PollControllerTriggers();

		// CONTINUOUS MIGRATION (Phase 3, v36): send THIS frame's continuous MoveIntent (raw direction + this frame's
		// dt). One input per render frame — the server integrates each by its dt. No prediction this phase (the render
		// is the raw decoded server position).
		SendHeldMovement(now, delta);

		var tPoll0 = Time.GetTicksUsec();
		_client?.Poll(now);
		_controlChannel?.Poll();
		var pollUsec = Time.GetTicksUsec() - tPoll0;

		if (_client?.Zone is not null && !_zoneBuilt)
		{
			BuildZone(_client.Zone);
			_zoneBuilt = true;
		}

		SyncNodeField();

		AdvanceAutopilot(now);
		SendStartupChat();
		RequestMetrics(now);

		var t0 = Time.GetTicksUsec();
		SampleRenderStates(now);
		RecordRenderTraceFrame(); // agent render-smoothness trace: sample the armed entity's per-frame render/auth position
		var t1 = Time.GetTicksUsec();
		UpdateEntities();
		UpdateFloatingDamageNumbers(delta);
		var t2 = Time.GetTicksUsec();
		UpdateCamera();
		UpdateMonsterHomeMarkers();
		UpdateServerPositionMarkers();
		UpdateTelegraphDecals();
		UpdateAimWedge();
		UpdateSkillshotAim(now);
		UpdateDuoVisuals();
		UpdateControllerAimArrow();
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
		// CONTROLLER (2026-07-04): any deliberate mouse move reclaims aim for the mouse (see PollControllerAim,
		// which reclaims it back for the right stick). Not consumed/returned — motion isn't a "handled" gesture,
		// it just updates which device the aim seam (TryGetAimWorldPoint) reads from this frame onward.
		if (@event is InputEventMouseMotion)
		{
			_aimSourceIsController = false;
		}

		// CONTROLLER (2026-07-04): discrete joypad buttons. Ignores chat focus entirely (a physical button can't
		// type text, unlike the keyboard bindings below, which keep their own _chatInput guards untouched).
		// LT/RT are AXES on XInput pads, not buttons, so RT (attack) and LT (skillshot hold) are polled per-frame
		// instead (PollControllerTriggers, near UpdateSkillshotAim) — they never arrive as InputEventJoypadButton.
		if (@event is InputEventJoypadButton { Pressed: true } joyButton && GetWindow().HasFocus())
		{
			switch (joyButton.ButtonIndex)
			{
				case JoyButton.A: // mirrors Space (dodge-roll)
					TryMovementAction(ActionId.DodgeRoll, "Roll!");
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.B: // mirrors J (jump)
					TryMovementAction(ActionId.Jump, "Jump!");
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.Y: // mirrors K (charge)
					TryMovementAction(ActionId.Charge, "Charge!");
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.X: // context interact: loot-all (mirrors F) if a loot window is open, else harvest (mirrors E)
					ControllerContextInteract();
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.LeftShoulder: // mirrors R (Unison Shield)
					SendDuoAbilityIfReady(DuoAbilityKind.Shield);
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.RightShoulder: // mirrors G (Laser Tether toggle)
					SendDuoAbilityIfReady(DuoAbilityKind.TetherToggle);
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.DpadDown: // mirrors V (Midpoint Detonation)
					SendDuoAbilityIfReady(DuoAbilityKind.Detonate);
					GetViewport().SetInputAsHandled();
					return;
				case JoyButton.Start: // mirrors Tab (toggle inventory)
					_hud?.ToggleInventory();
					GetViewport().SetInputAsHandled();
					return;
			}
		}

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

			// COMBAT (LMB attack — the PRIMARY attack binding since the 2026-07-03 combat-keys decision; Space is
			// now the dodge-roll): LEFT-mouse-down triggers the free-aim melee swing (server-authoritative; the aim
			// is the player→cursor bearing). This handler is _UnhandledInput, so any HUD/panel control the cursor is
			// over has already consumed the click (it never reaches here) — the swing only fires on a click into the
			// 3D world. RIGHT mouse stays the hold-to-move poll (untouched). Consumed so it doesn't fall through.
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

		// CONTINUOUS MIGRATION (Phase 4): the Alt+Shift+R "reset reconcile counters" and Alt+R "Force Resync"
		// hotkeys drove the deleted tile LocalPlayerPredictor (ResetReconcileCounters / ForceResync). The continuous
		// predictor has no step-seq tallies and reconciles every snapshot, so both are gone.

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

		// LIVESPEED-DESYNC P2: F loots all while the corpse loot window is open (and chat isn't focused) — the same
		// intent as the "Loot All [F]" footer button. F is otherwise unbound (F1/F3/F11 are distinct keycodes; E
		// harvests, Space dodges), so this only acts when a corpse window is up and never disturbs other bindings.
		if (key.Keycode == Key.F && _chatInput?.HasFocus() != true && _hud?.Loot is { IsOpen: true } lootAllWindow)
		{
			lootAllWindow.RaiseLootAllRequested();
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

		// COMBAT KEYS (user decision 2026-07-03, the genre-standard action mapping): LMB = attack (see the
		// mouse handler — it was already bound and is now the PRIMARY attack), Space = DODGE-ROLL (was L, "a bit
		// far" for the most reflex-critical action; Space was attack's original S2B key and is freed by LMB).
		// Not while typing in chat (Space types a space in a message instead of rolling).
		if (key.Keycode == Key.Space && _chatInput?.HasFocus() != true)
		{
			TryMovementAction(ActionId.DodgeRoll, "Roll!");
			GetViewport().SetInputAsHandled();
			return;
		}

		// MOVEMENT-ACTIONS Phase B1 — TEMPORARY DEV TRIGGER: J fires a ballistic jump along the local player's current
		// facing. This is a runtime, in-client trigger (no launch flag, no restart — the project's live-toggle
		// discipline); B1 has no skill bar yet. Phase E REPLACES this with real skill-input binding. J is otherwise
		// unbound (distinct from WASD movement, E harvest, F loot-all, Space dodge, F1/F3/F11 panels, Tab/Enter/T
		// chat). Not while typing in chat (so 'j' types a letter instead of jumping). The action is server-confirmed
		// in B1 (no client prediction) — a brief delay before the avatar rises is expected and correct for B1.
		if (key.Keycode == Key.J && _chatInput?.HasFocus() != true)
		{
			TryMovementAction(ActionId.Jump, "Jump!");
			GetViewport().SetInputAsHandled();
			return;
		}

		// MOVEMENT-ACTIONS Phase D — TEMPORARY DEV TRIGGER (Phase E replaces these with real skill-input binding,
		// exactly like J): K fires a CHARGE (a fast forward dash along the aim/facing heading, early-stopping at
		// walls/bodies). The DODGE-ROLL moved to Space (see the combat-keys block above). Both ride the SAME
		// client-predicted action stream the jump uses (MmoClient.SendAction → BeginAction; one-at-a-time + the
		// mirrored cooldown decline locally). Not while typing in chat.
		if (key.Keycode == Key.K && _chatInput?.HasFocus() != true)
		{
			TryMovementAction(ActionId.Charge, "Charge!");
			GetViewport().SetInputAsHandled();
			return;
		}

		// DUO-WAVE2 (exp/duo-abilities) — co-op ability triggers (TEMPORARY DEV KEYS, like J/K; Phase E binds real
		// skill inputs). R = Unison Shield (ability 2), G = Laser Tether toggle (ability 3), V = Midpoint Detonation
		// initiate/confirm (ability 4). Each is a discrete press routed to the server's DuoAbility stream (server-
		// authoritative — no client prediction). Not while typing in chat.
		if (_chatInput?.HasFocus() != true && key.Keycode is Key.R or Key.G or Key.V)
		{
			var ability = key.Keycode switch
			{
				Key.R => DuoAbilityKind.Shield,
				Key.G => DuoAbilityKind.TetherToggle,
				_ => DuoAbilityKind.Detonate,
			};
			SendDuoAbilityIfReady(ability);
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

	// Shared by the R/G/V keyboard bindings (above) and the LB/RB/D-pad-Down controller bindings: send a duo-
	// ability intent when a client is attached and logged in. Keyboard callers additionally gate on chat focus
	// themselves (unchanged behaviour); joypad callers don't — a shoulder-button press can't type text.
	private void SendDuoAbilityIfReady(DuoAbilityKind ability)
	{
		if (_client is not null && _client.IsLoggedIn)
		{
			_client.SendDuoAbility(ability);
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

		// Play the LOCAL cat's kick (attack) animation on the swing — cosmetic; the server stays authoritative on
		// damage. Remote players' kicks would need a replicated attack event (not wired yet).
		if (_client.LocalNetworkId is uint localAttackerId && _renderer is not null
			&& _renderer.TryGetActiveVisual(localAttackerId, out var attackerVisual)
			&& attackerVisual is PlayerVisual localPlayerVisual)
		{
			localPlayerVisual.TriggerAttack();
		}

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

	// MOVEMENT-ACTIONS Phase B2 / D — TEMPORARY DEV TRIGGER (Phase E replaces it with real skill-input binding):
	// trigger a movement action (jump / charge / dodge-roll) on the action stream (MmoClient.SendAction, its OWN
	// cursor) — CLIENT-PREDICTED (Model A): the action moves the local avatar INSTANTLY (predicted), leading the
	// server by ~RTT along the same path, and the reconcile keeps it glued unless the server rejects. The launch
	// HEADING is the cursor aim (or the discrete facing), quantized via the SAME shared AimAngle the attack aim uses,
	// so client predict + server execute decode the identical unit heading (for the dodge-roll too — a roll goes
	// where you aim, the minimal dev binding; a roll-along-held-WASD variant is Phase E UX). CONTROLLER (2026-07-05):
	// on pad, a deflected LEFT stick overrides that — the action follows the movement bearing (see the in-body
	// comment); keyboard+mouse resolution is unchanged when the stick is centered. SendAction returns null
	// when the trigger is DECLINED locally (one-at-a-time / mirrored cooldown) — nothing was sent and we show no
	// feedback, mirroring the server's can-act reject.
	private void TryMovementAction(ActionId actionId, string feedback)
	{
		if (_client?.IsLoggedIn != true)
		{
			return;
		}

		// CONTROLLER (2026-07-05 feel-test): while the LEFT (movement) stick is deflected, the action heading is
		// the stick's continuous bearing — a dash goes where you're STEERING, not where you aim (a pad user aiming
		// right while running left expects the roll to follow the run; "dash toward aim" read wrong in play). The
		// twin-stick split is deliberate: the left stick launches the action while facing stays on the RIGHT-stick
		// aim (shoot one way, roll another — see UpdateLocalContinuousFacing). Stick centered (or no pad) -> the
		// existing aim-seam resolution below, verbatim, so keyboard+mouse behavior is untouched.
		float headingRadians;
		if (CurrentControllerMoveHeading() is WorldVector stickHeading)
		{
			// World bearing of the unit stick vector (+X east, +Y south) — the same atan2(dz, dx) convention
			// TryGetAimToCursor produces, so AimAngle.Quantize decodes it identically on both sides.
			headingRadians = Mathf.Atan2((float)stickHeading.Y, (float)stickHeading.X);
		}
		else
		{
			// Aim the action along the cursor when available (so the dash/arc goes where the player is looking),
			// else fall back to the discrete facing — exactly the aim source TryAttack uses, so the heading is
			// always defined.
			headingRadians = TryGetAimToCursor(out var cursorAim)
				? cursorAim
				: LocalFacingRadians();
		}

		if (_client.SendAction((byte)actionId, AimAngle.Quantize(headingRadians)) is not null)
		{
			ShowInteractFeedback(feedback);
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
					tuning.RadiusUnits,
					FreeAimSector.EntityHitRadiusTiles,
					state.Position.X,
					state.Position.Y))
			{
				continue;
			}

			// BOSS legibility (2026-07-05 feel-test): the server EXCLUDES the attacker from its own DamageEventMessage
			// broadcast for a melee hit (BroadcastDamageEvent's excludeSession — see GameServer.cs), so for the
			// attacker's OWN swing this predicted pop is the ONLY feedback that ever arrives, in EITHER protected phase
			// (P1 plating still reduces to a real server chip number, but that number never reaches the attacker; P3
			// ward sends no DamageEvent to anyone at all). This predictor has no way to know the server's true reduced
			// P1 amount without a protocol change (out of scope here), so a protected victim always shows "IMMUNE"
			// rather than guess a number that might be wrong — the honest signal is "your swing didn't land," not a
			// possibly-fabricated chip number. A non-protected victim is unaffected (full predicted amount, as before).
			var deflect = _client.IsBossProtected(state.NetworkId);
			// BOSS legibility: which protected phase this is, so the predicted deflect says the honest word — P3 ward
			// (<= the split) shows "IMMUNE" (truly 0), P1 plating shows "TURNED" (chip still lands; a false "IMMUNE"
			// would contradict the boss's dropping health bar). Same 0.5 split the teach label uses (the two protected
			// windows are >70% and <=40%, so they never overlap around it).
			var warded = state.HealthFraction <= 0.5f;

			// Pop the number at the victim's live visual (same path/position as the server-driven number). Fall back
			// to the render-state XZ if no visual is bound this frame, so the prediction still shows.
			if (_renderer.TryGetActiveVisual(state.NetworkId, out var visual))
			{
				if (deflect)
				{
					_floatingText.SpawnDeflected(visual.Position, 0, warded);
				}
				else
				{
					_floatingText.Spawn(visual.Position, tuning.Damage);
				}
			}
			else
			{
				var fallbackPosition = new Vector3((float)state.Position.X, 0f, (float)state.Position.Y);
				if (deflect)
				{
					_floatingText.SpawnDeflected(fallbackPosition, 0, warded);
				}
				else
				{
					_floatingText.Spawn(fallbackPosition, tuning.Damage);
				}
			}
		}
	}

	// FREEAIM: the continuous player→cursor world bearing in radians (atan2(dz, dx), +X east / +Z south — the same
	// convention the shared AimAngle uses and the server's sector resolver reduces against). Returns false before
	// login, when there is no local render position yet, or when the ground ray misses; in the dead-zone (cursor on
	// the player) it still returns a (possibly noisy) bearing — an attack always has an aim, unlike movement which
	// stops in the dead-zone.
	//
	// CONTROLLER (2026-07-04): this is THE single aim-source seam every cursor-aim caller already shares (melee
	// attack, movement-action heading, skillshot hold-aim) — it now asks TryGetAimWorldPoint for the point to aim
	// at instead of raycasting the mouse cursor directly, so all of them pick up controller aim for free.
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

		if (!TryGetAimWorldPoint(out var hit))
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

	// CONTROLLER (2026-07-04): the aim-source seam. Mouse mode (the right stick hasn't aimed since the last mouse
	// move, or never has): the existing cursor -> ground-plane raycast, byte-identical to the pre-controller
	// behaviour. Controller mode (the right stick last moved past its deadzone, see PollControllerAim): a
	// SYNTHETIC point — the local player's world position + _controllerAimDirection * ControllerAimProjectDistance
	// — so this always hands back an actual world point, the same shape TryGetAimToCursor's caller (above) already
	// consumes (a player -> point bearing). Distance is otherwise inert for an angle-only consumer (atan2 cancels a
	// uniform scale) but keeping the seam POINT-shaped, not angle-shaped, mirrors the mouse branch's real primitive.
	// Returns false when controller mode has no local render position yet, or mouse mode's ground ray misses.
	private bool TryGetAimWorldPoint(out Vector3 point)
	{
		if (_aimSourceIsController)
		{
			if (!TryGetLocalRenderPosition(out var px, out var pz))
			{
				point = default;
				return false;
			}

			point = new Vector3(
				px + ((float)_controllerAimDirection.X * ControllerAimProjectDistance),
				0f,
				pz + ((float)_controllerAimDirection.Y * ControllerAimProjectDistance));
			return true;
		}

		var screenPosition = GetViewport().GetMousePosition();
		return TryPickGroundPoint(screenPosition, out point);
	}

	// CONTROLLER (2026-07-04): poll the first connected joypad's RIGHT stick for aim, once per frame (called from
	// _Process, UNGATED by chat focus — a stick tilt can't type text). Beyond the shared radial deadzone, the
	// stick's direction becomes the persistent aim direction (normalized) and claims aim-source ownership from the
	// mouse (see TryGetAimWorldPoint); inside the deadzone the AIM state is a no-op — the LAST aim source stays in
	// effect, same "dead-zone holds the previous heading" idea the mouse path already uses (CursorHeading).
	// CONTROLLER AIM-FACING (2026-07-05): additionally refreshes _controllerAimStickActive every frame (true only
	// while the stick is deflected NOW) — the momentary predicate the local facing override keys off, as opposed
	// to the persistent ownership flag above.
	private void PollControllerAim()
	{
		if (!TryGetJoyAxisVector(JoyAxis.RightX, JoyAxis.RightY, out var rightX, out var rightY))
		{
			_controllerAimStickActive = false;
			return;
		}

		_controllerAimStickActive = true;
		// LIVE FEEL FIX (2026-07-05, user repro: "the art of the character is not facing the correct direction"):
		// the right stick's axes are SCREEN-relative, exactly like the left stick's — so they need the SAME 45°
		// screen->world isometric rotation CurrentControllerMoveHeading applies (worldDx = x+y, worldDz = y-x).
		// The first cut fed the raw stick vector in as if it were already world-space, leaving every aim consumer
		// (facing, arrow, attack wedge, skillshot) rotated 45° from where the stick pointed on screen. The mouse
		// path never has this problem: the cursor raycast goes through the camera projection, which IS the
		// screen->world transform.
		_controllerAimDirection = new WorldVector(rightX + rightY, rightY - rightX).Normalized();
		_aimSourceIsController = true;
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

	// NODE-FIELD N3 (docs/node-field-design.md D5/D6): the E-press harvest key now has TWO candidate pools —
	// the catalogue field (nodes, via NodeFieldTargeting/HarvestNodeMessage) and the entity list (corpses,
	// via the unchanged HarvestTargeting/InteractRequest). Both share the SAME reach; we resolve each pool's
	// own nearest candidate independently, then send whichever of the two is actually closer (the pre-N3
	// behaviour picked one "nearest interactable" across a single unified list, so this preserves that intent
	// for the rare case where a corpse and an available node are BOTH in reach at once).
	private void TryHarvest()
	{
		// CONTINUOUS MIGRATION (Phase 9): targeting reads the SERVER-CONFIRMED continuous position (off-grid,
		// sub-tile), NOT the predicted render position — so the client's Euclidean reach check matches the server's
		// interact gate (S53: targeting uses confirmed state).
		if (_client?.IsLoggedIn != true || _client.LocalConfirmedPosition is not WorldVector actorPosition)
		{
			return;
		}

		// _renderStates is refreshed every frame in SampleRenderStates; it is the same data the renderer
		// sees, so nearest-corpse selection matches what the player is looking at.
		var hasCorpse = HarvestTargeting.TryFindNearestCorpse(
			_renderStates, actorPosition, out var corpseNetworkId, out var corpseDistanceSquared);

		var hasNode = false;
		ushort nodeIndex = 0;
		var nodeDistanceSquared = double.MaxValue;
		if (_nodeFieldChunkIndex is { } chunkIndex && _client.NodeCatalog is not null)
		{
			hasNode = NodeFieldTargeting.TryFindNearestAvailableNode(
				chunkIndex, _client.DepletedNodeIndices, actorPosition, out nodeIndex, out nodeDistanceSquared);
		}

		if (hasNode && (!hasCorpse || nodeDistanceSquared <= corpseDistanceSquared))
		{
			_client.SendHarvestNode(nodeIndex);
		}
		else if (hasCorpse)
		{
			_client.SendInteractRequest(corpseNetworkId);
		}
		else
		{
			// Nothing adjacent: give immediate local feedback rather than a silent no-op. This is the one
			// place the client "knows" without the server, and it never mutates state — purely a hint.
			ShowInteractFeedback("No resource node in reach.");
		}
	}

	// CONTROLLER (2026-07-04): X = context interact — one button standing in for the two keyboard bindings that
	// already key off "is the loot window open" (F = loot-all) vs not (E = harvest); mirrors both bodies byte for
	// byte. Its own helper (rather than reusing E/F's handlers directly) because the controller has one button
	// where keyboard has two independent keys — E and F stay untouched.
	private void ControllerContextInteract()
	{
		if (_hud?.Loot is { IsOpen: true } lootWindow)
		{
			lootWindow.RaiseLootAllRequested();
		}
		else
		{
			TryHarvest();
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
			MouseHeadingDeadZoneUnits,
			MouseHeadingHysteresisDegrees);
	}

	// FREE-ANGLE A/B TEST: the free-angle counterpart to CurrentMouseHeading — the RAW player->cursor unit heading
	// (any angle, no octant snap, no hysteresis) while the RIGHT mouse button is held, or null when the button is up,
	// before login, with no render position, on a missed ground ray, OR inside the dead-zone (caller stops). Uses the
	// SAME continuous origin (predictor-tweened render position), the SAME ground pick, the SAME dead-zone constant,
	// and the SAME injected/autopilot pre-emption as CurrentMouseHeading — only the octant/hysteresis snap is dropped
	// so the heading is the exact cursor direction. Called from SendHeldMovement in place of CurrentMouseHeading when
	// _freeAngleMovement is on.
	private WorldVector? CurrentFreeAngleMouseHeading()
	{
		if (!Input.IsMouseButtonPressed(MouseButton.Right))
		{
			return null;
		}

		if (_client?.IsLoggedIn != true)
		{
			return null;
		}

		if (!TryGetLocalRenderPosition(out var playerX, out var playerZ))
		{
			return null;
		}

		var screenPosition = GetViewport().GetMousePosition();
		if (!TryPickGroundPoint(screenPosition, out var hit))
		{
			return null;
		}

		// A deliberate mouse move overrides any debug-injected/autopilot motion so they never fight (mirrors
		// CurrentMouseHeading).
		if (_injectedDirection.HasValue || _autopilotPattern is not null)
		{
			_injectedDirection = null;
			StopAutopilot();
		}

		var dx = hit.X - playerX;
		var dy = hit.Z - playerZ;
		return CursorHeading.FreeAngleFromWorldVector(dx, dy, MouseHeadingDeadZoneUnits);
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

	// DUO-SKILLSHOT (exp/duo-abilities): the render position of ANY entity by network id (generalizes
	// TryGetLocalRenderPosition), for drawing a partner's intercept-preview line from the partner's position.
	private bool TryGetRenderPosition(uint networkId, out float x, out float z)
	{
		x = 0f;
		z = 0f;
		foreach (var state in _renderStates)
		{
			if (state.NetworkId == networkId)
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

		// AGX-TONEMAP: match Blender's default AgX view transform. Godot defaults to Linear tonemapping, which reads
		// over-saturated / "too yellow" vs Blender's muted browns; AgX rolls off + desaturates the warm tones the same
		// way. Scene-wide via a WorldEnvironment. A modest neutral ambient keeps the non-toon models' shadow sides
		// readable (the cat's shader disables ambient itself); the sun stays the key light.
		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.36f, 0.40f, 0.45f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.55f, 0.55f, 0.60f),
			AmbientLightEnergy = 0.35f,
			TonemapMode = Godot.Environment.ToneMapper.Agx
		};
		AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = environment });

		_camera = new Camera3D
		{
			Name = "Camera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = CameraSize,
			Position = CameraRigOffset
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

		// Live MOVE-trace toggle (N-movement-trace-live-toggle) — flips the console mmo_trace output on/off while
		// running, like the two toggles above. Seeded from the client (MMO_DEBUG_MOVEMENT initial value) after
		// connect in _Ready; the panel is built before the client exists, so it starts unchecked here.
		var movementTrace = new CheckBox { Name = "MovementTrace", Text = "Movement trace (console)" };
		movementTrace.AddThemeFontSizeOverride("font_size", 13);
		movementTrace.Toggled += ApplyMovementTrace;
		rows.AddChild(movementTrace);
		_movementTraceCheck = movementTrace;

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

		// S96 live toggle — flips on click, no Apply needed: render every Player (local + remote) as the "Cato"
		// AnimatedSprite3D billboard (idle/walk PNG frames, side-view directional flip) instead of the character
		// model. Toggling rebuilds already-spawned players so the swap is immediate. Falls back to the box if the
		// Cato art isn't imported yet.
		var catoSprite = new CheckBox { Name = "CatoSprite", Text = "Cato sprite (player)", ButtonPressed = _tuning.DebugCatoSprite };
		catoSprite.AddThemeFontSizeOverride("font_size", 13);
		catoSprite.Toggled += ApplyCatoSprite;
		rows.AddChild(catoSprite);

		// CONTINUOUS MIGRATION COMPLETE: the old "Prediction (predict local player)" A/B raw-vs-predicted toggle was
		// removed here — prediction is now THE model, always on, and the raw-confirmed-render fallback it flipped to was
		// dev-only cruft. The local player always renders the predictor's smooth position; the predictor is always
		// attached (EnsurePredictor on every lifecycle seam). No checkbox, no MmoClient.PredictionEnabled.

		// Spawner tiles: debug viz of the monster spawner anchors (red tiles), default off — like server-positions.
		var spawnerTiles = new CheckBox { Name = "SpawnerTiles", Text = "Spawner tiles", ButtonPressed = _showSpawnerTiles };
		spawnerTiles.AddThemeFontSizeOverride("font_size", 13);
		spawnerTiles.Toggled += ApplySpawnerTiles;
		rows.AddChild(spawnerTiles);

		// Server positions: debug viz of every REMOTE entity's authoritative server tile (cyan tiles), default off —
		// like prediction/spawner tiles. The remote-entity analogue of "Prediction tiles": shows the interpolation
		// gap between where the server says an entity (e.g. a slime) is and where its body is rendered.
		var serverPositions = new CheckBox { Name = "ServerPositions", Text = "Server positions", ButtonPressed = _showServerPositions };
		serverPositions.AddThemeFontSizeOverride("font_size", 13);
		serverPositions.Toggled += ApplyServerPositions;
		rows.AddChild(serverPositions);

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
	// blend + smoothing + teleport-snap. All live (no restart) via the same Apply-all / live-toggle pattern.
	// (CONTINUOUS MIGRATION Phase 4: the tile-predictor "Stop on reversal" toggle and "Force Resync" button were
	// removed with LocalPlayerPredictor; the "track predicted tile" camera toggle went with the continuous swap —
	// there is no predicted TILE anymore.)
	private void BuildMovementTab(TabContainer tabs)
	{
		var rows = AddDebugTab(tabs, "Movement");

		var note = CreateOverlayLabel("MovementSpeedNote", 12);
		// Two free multipliers (replaced the discrete-bracket dropdown): a GLOBAL one that scales the base move speed
		// live via continuous.baseMoveSpeed = defaultBase × multiplier (player + bots + every entity), and a PER-PLAYER
		// one that sends /speed <multiplier> to scale ONLY the local player on top of the global base. 1.0 = default.
		note.Text = "Speed — Global × scales base (player + bots); My speed × scales only the local player on top.";
		rows.AddChild(note);

		// FREE-ANGLE A/B TEST — live client-local toggle (no Apply, no restart, no server round-trip, per the
		// live-toggle rule). OFF (default) = the current 8-direction movement. ON = the MOUSE hold-to-walk follows
		// the cursor at any angle (WASD stays 8-way in both). Purely input/presentation — flips instantly while
		// playing so both modes can be A/B'd back-to-back.
		var freeAngle = new CheckBox { Name = "FreeAngleMovement", Text = "Free-angle movement (follow mouse)", ButtonPressed = _freeAngleMovement };
		freeAngle.AddThemeFontSizeOverride("font_size", 13);
		freeAngle.Toggled += ApplyFreeAngleMovement;
		rows.AddChild(freeAngle);

		// Applied on Apply/Enter:
		// GLOBAL speed as a single free multiplier of the base (replaced the discrete-bracket dropdown). On apply it
		// sends continuous.baseMoveSpeed = defaultBase × multiplier (see OnMovementApplyPressed), so it retunes the
		// player, the bots, and every entity live. Seeded to 1.0 by SeedMovementFields.
		_moveSpeedMultiplier = AddTuningField(rows, "Global speed (× multiplier)", OnMovementApplyPressed);
		// PER-PLAYER speed as a free multiplier of the local player only (replaced the dropdown's per-entity /speed
		// send). On apply it sends the /speed <multiplier> server command (see OnMovementApplyPressed), setting only the
		// local player's per-entity SpeedMultiplier — multiplies on top of the global base. Seeded to 1.0.
		_movePlayerSpeedMultiplier = AddTuningField(rows, "My speed (× multiplier)", OnMovementApplyPressed);
		// S93: artificial one-way network latency (ms each way). 0 = off (default I/O path). Felt RTT ≈ 2× this.
		_moveNetLatencyMs = AddTuningField(rows, "Net latency (ms, each way)", OnMovementApplyPressed);
		// S95: camera follow smoothing as a per-second rate (frame-rate independent). 0 = off/hard-follow. CONTINUOUS
		// default 10 (the good-feeling experiment value) — tracks tightly; lower values trail/coast after a stop. The
		// field is seeded from _cameraSmoothing (=10) by SeedMovementFields.
		_moveCameraSmoothing = AddTuningField(rows, "Camera smoothing (/s, 0=off)", OnMovementApplyPressed);
		// S102 new: camera teleport-snap distance (tiles). Beyond this single-frame jump the camera hard-snaps
		// (respawn / zone change) instead of gliding; below it the smoothing glides. Was the const = 4.
		_moveCameraTeleportSnapTiles = AddTuningField(rows, "Camera teleport-snap (tiles)", OnMovementApplyPressed);
		// remote-interp-tighten Part A: the REMOTE jitter-buffer (ms) — how far behind its true server tile a remote
		// entity (slime, other players) renders. Lower = tighter to the cyan server marker; raise = smoother under
		// jitter. Blank / < 0 = computed default (max(0.5*cadence, 50ms)). Applied live to all remote interpolators.
		// KEEP — a legitimate remote-smoothness feel knob (NOT tile-era cruft): the remote-entity playout-buffer delay
		// in ms (how far behind its true server position a slime / other player renders). Higher = smoother under
		// arrival jitter but laggier; lower = tighter to the cyan server marker. Blank / < 0 = computed auto default.
		_moveRemoteInterpBufferMs = AddTuningField(rows, "Remote interp buffer (ms playout delay; <0=auto)", OnMovementApplyPressed);

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
		_combatRadiusTiles = AddTuningField(rows, "radius (units)", OnCombatApplyPressed);
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

		// DATA-DRIVEN: the selected type's fields are built here at seed-time from the replicated MonsterTuningField
		// list (one labelled LineEdit row per field) into this container — no hardcoded per-field members.
		_monsterFieldRows = new VBoxContainer { Name = "MonsterFieldRows" };
		_monsterFieldRows.AddThemeConstantOverride("separation", 4);
		rows.AddChild(_monsterFieldRows);

		// Apply (tune live, in-memory) + Save (persist to the manifest so it survives a restart) side by side.
		var buttonRow = new HBoxContainer { Name = "Row_MonsterButtons" };
		buttonRow.AddThemeConstantOverride("separation", 8);
		var apply = new Button { Name = "MonsterApply", Text = "Apply" };
		apply.AddThemeFontSizeOverride("font_size", 14);
		apply.Pressed += OnMonsterApplyPressed;
		buttonRow.AddChild(apply);
		// MONSTER-TUNING-SAVE: persist the current live values to Content/monsters.json (server admin-gates the write).
		var save = new Button { Name = "MonsterSave", Text = "Save" };
		save.AddThemeFontSizeOverride("font_size", 14);
		save.Pressed += OnMonsterSavePressed;
		buttonRow.AddChild(save);
		rows.AddChild(buttonRow);
	}

	// MONSTER-TUNING-SAVE: persist the current live-tuned monster values to the manifest so they survive a restart.
	// Admin-gated client-side for clarity (the server also admin-gates the write); sends the parameterless
	// SaveMonsterTuningMessage. The server replies with a "saved monster tuning to <path>" system line.
	private void OnMonsterSavePressed()
	{
		if (_client?.Role != ClientRole.Admin)
		{
			return;
		}

		_client.SendSaveMonsterTuning();
		ShowInteractFeedback("Monster tuning save requested.");
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

		// PLAYER-COLLISION-TOGGLE: a live, server-authoritative toggle for player↔player collision. Flips on click (no
		// Apply) — sends an admin-gated request; the server flips its authoritative flag and broadcasts the new value to
		// ALL clients, so both the server integrator's gather and every client predictor's gather flip TOGETHER (parity).
		// Monster collision (player↔monster + monster↔monster) is UNAFFECTED. Re-seeded to the replicated value on each
		// panel open. Sits on the Server tab since it is a server rule (admin-only panel), separate from the Apply fields.
		var collisionHeader = CreateOverlayLabel("TuningCollisionHeader", 12);
		collisionHeader.Text = "— server rules (instant) —";
		rows.AddChild(collisionHeader);
		var playerCollision = new CheckBox { Name = "PlayerCollision", Text = "Player-vs-player collision", ButtonPressed = _client?.PlayerCollisionEnabled ?? true };
		playerCollision.AddThemeFontSizeOverride("font_size", 13);
		playerCollision.Toggled += OnPlayerCollisionToggled;
		rows.AddChild(playerCollision);
		_playerCollisionCheck = playerCollision;
	}

	// PLAYER-COLLISION-TOGGLE: the F1 Server-tab checkbox flipped — send the admin-gated request live. The server
	// admin-gates it, flips its authoritative flag, and broadcasts the new value back (which re-seeds the client's
	// replicated copy); the client never predicts the flip itself. A brief 1-tick prediction blip at the flip instant
	// (message round-trip) is accepted. A non-admin never reaches here (the F1 panel is admin-only).
	private void OnPlayerCollisionToggled(bool enabled) => _client?.SendSetPlayerCollision(enabled);

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

	// Live MOVE-trace toggle (N-movement-trace-live-toggle), shared by the F3 perf-panel checkbox: flips the
	// console mmo_trace output on/off in the running client. State lives on MmoClient (its trace object);
	// snapshot tracking for the F3 HUD is unconditional either way — this gates only the console lines.
	private void ApplyMovementTrace(bool enabled)
	{
		if (_client is not null)
		{
			_client.DebugMovementEnabled = enabled;
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

	// FREE-ANGLE A/B TEST — F1 Movement "Free-angle movement" live toggle. Flips the client-local flag that
	// SendHeldMovement reads each frame to pick the mouse heading source (8-dir octant vs raw cursor unit vector).
	// Client-local only: no message, no server round-trip — the wire already carries a float heading either way.
	private void ApplyFreeAngleMovement(bool enabled)
	{
		_freeAngleMovement = enabled;
	}

	// F1 Visual "Server positions" live toggle — show/hide the cyan remote-entity server-tile debug markers (default
	// off). The rendering is gated in UpdateServerPositionMarkers (which frees the markers next frame when off); this
	// just flips the flag.
	private void ApplyServerPositions(bool enabled)
	{
		_showServerPositions = enabled;
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

		// M2 perf gate (docs/town-floor1-blockout-design.md): wall-clock the whole static zone build
		// (floor + walls) so the <250 ms @ 384x384 budget is checkable straight from the log line below.
		var buildTimer = System.Diagnostics.Stopwatch.StartNew();

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
		// M2 (town-blockout): an AUTHORED zone (genVersion 2+) carries per-tile surface categories — paint those
		// as flat graybox colors instead of the terrain.png path (which genVersion 1 keeps unchanged).
		var paintedFloor = zone.Authored is { } authoredMap
			? Mmo.Client.Godot.Visuals.TerrainPainter.BuildAuthoredFloor(_worldRoot, authoredMap)
			: Mmo.Client.Godot.Visuals.TerrainPainter.BuildFloor(_worldRoot, zone.Width, zone.Height);
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

			// M2 water decision: authored Water tiles are blocked but paint as a flat blue floor — a gray box
			// standing on a pond reads wrong, so they get NO wall box (still impassable, server-authoritative).
			// Authored out-of-world padding likewise draws nothing at all. genVersion 1 is unaffected (always true).
			if (!AuthoredSurfaceVisuals.ShouldDrawWallBox(zone.Authored, tile))
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
				InstanceCount = wallTiles.Count // allocates the buffer; MUST precede the Buffer assignment
			};
			// M2 perf: ONE bulk Buffer upload per chunk (12 floats/instance, identity basis at TileToWorld) instead
			// of a per-instance SetInstanceTransform interop call each — same transforms, one marshal.
			wallMultiMesh.Buffer = MultiMeshTileBuffer.PackUprightTileTransforms(wallTiles, 0.4f);

			var wallChunk = new Node3D { Name = $"WallChunk_{cx}_{cz}" };
			wallChunk.AddChild(new MultiMeshInstance3D
			{
				Name = "WallTiles",
				Multimesh = wallMultiMesh,
				MaterialOverride = _wallMaterial
			});
			_wallRoot.AddChild(wallChunk);
		}

		// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1): client-only grass/flower/
		// pebble decor, built from the SAME authored map the floor above was just painted from. D1 gate
		// ("genVersion 1 zones: NO decor") falls straight out of Authored being null on non-authored zones —
		// no separate check needed. Timed separately so the decor cost is visible on its own in the print
		// line below without disturbing the existing floor+walls M2 budget measurement.
		var decorTimer = System.Diagnostics.Stopwatch.StartNew();
		var decorInstanceCount = 0;
		if (zone.Authored is { } authoredMapForDecor)
		{
			(_, decorInstanceCount) = Mmo.Client.Godot.Visuals.DecorPainter.BuildDecor(_worldRoot, authoredMapForDecor, zone.Seed);
		}
		var decorMs = decorTimer.Elapsed.TotalMilliseconds;

		// NODE-FIELD N3 (docs/node-field-design.md D6): the harvestable field — chunked MultiMeshes built from
		// the SAME NodeCatalog mirror MmoClient's HandleZoneInfo already computed (N2) + verified against
		// ZoneInfo.CatalogHash. Timed separately, same discipline as decor above. A non-authored (genVersion 1)
		// zone's catalogue is the trivial empty one (N2), so this naturally renders nothing — mirrors the D1
		// decor gate without a separate check.
		var nodeFieldTimer = System.Diagnostics.Stopwatch.StartNew();
		var nodeFieldInstanceCount = 0;
		if (_client?.NodeCatalog is { } nodeCatalog && nodeCatalog.Entries.Count > 0)
		{
			var chunkIndex = NodeFieldChunkIndex.Build(nodeCatalog);
			var placements = NodeFieldPlacer.PlaceAll(nodeCatalog, zone.Seed);
			_nodeFieldChunkIndex = chunkIndex;
			_nodeFieldPlacements = placements;
			_nodeFieldView = NodeFieldView.Build(_worldRoot, chunkIndex, placements, _client.DepletedNodeIndices);
			_lastSyncedNodeFieldVersion = _client.NodeFieldVersion;
			nodeFieldInstanceCount = _nodeFieldView.InstanceCount;
		}
		var nodeFieldMs = nodeFieldTimer.Elapsed.TotalMilliseconds;

		// M2 perf gate: the one-line budget check (target <250 ms at 384x384; see docs/town-floor1-blockout-design.md).
		// P2 extends the same line with the decor sub-cost + instance count; N3 extends it again with the node-field
		// sub-cost + instance count, so the <250 ms total budget (floor+walls+decor+field) stays checkable from one
		// log line.
		GD.Print("M2 zone build (floor+walls+decor+field): " +
			$"{buildTimer.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms " +
			$"(decor {decorMs.ToString("F1", CultureInfo.InvariantCulture)} ms, {decorInstanceCount} instances) " +
			$"(field {nodeFieldMs.ToString("F1", CultureInfo.InvariantCulture)} ms, {nodeFieldInstanceCount} instances) " +
			$"({zone.Width}x{zone.Height}, genVersion {zone.GenVersion})");

		// S109: hand the HUD minimap a READ-ONLY snapshot of the static map (extents + wall set) so it can bake its
		// simplified top-down raster ONCE. This is the same seed-regenerated ZoneModel the 3D world is built from
		// (read-only — no movement/world state is mutated). The Generation bumps per zone so the minimap re-bakes.
		// N: also pass zone.Authored — the SAME authored map the floor above was just painted from — so an
		// authored (genVersion 2+) zone's minimap base layer reads the real ground truth, not terrain.png.
		_minimapGeneration++;
		_hudState.Map = new Mmo.Client.Godot.UI.HudState.MinimapMap(
			zone.Width, zone.Height, zone.BlockedTiles, zone.Authored, _minimapGeneration);
	}

	// NODE-FIELD N3 (docs/node-field-design.md D6): a NodeState/NodeStateBatch flip bumps MmoClient
	// .NodeFieldVersion — cheap to poll (a single int compare in the common no-change case) so this runs every
	// frame rather than needing a push/event hook. On a real change, NodeFieldView.SyncDepletion itself diffs
	// the depleted set and rebuilds only the affected chunk(s). No-op before the field has been built (a
	// non-authored zone, or before BuildZone has run yet).
	private void SyncNodeField()
	{
		if (_client is not { } client || _nodeFieldView is not { } view)
		{
			return;
		}

		if (client.NodeFieldVersion == _lastSyncedNodeFieldVersion)
		{
			return;
		}

		view.SyncDepletion(client.DepletedNodeIndices);
		_lastSyncedNodeFieldVersion = client.NodeFieldVersion;
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
		if (_hasLocalRender && _hasPrevRenderPos && _renderFrameDelta > MotionSnapJumpUnits
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
					// BOSS legibility (2026-07-05 feel-test): a hit that reaches a currently-PROTECTED boss (P1 plating
					// / P3 ward) must read as "bounced off," never "chip damage" — route it to the deflected render
					// instead of a normal red number. Every non-boss victim (and a non-protected boss) is untouched.
					if (_client.IsBossProtected(damage.NetworkId))
					{
						_floatingText.SpawnDeflected(visual.Position, damage.Amount);
					}
					else
					{
						_floatingText.Spawn(visual.Position, damage.Amount);
					}
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
		// CONTINUOUS: focus on the cosmetic (continuous) render position, temporally smoothed (frame-rate independent).
		// smoothing 10 = a tight follow with a small cosmetic glide; the former confirmed-tile blend leg is removed.
		double cosX = localState.Position.X, cosY = localState.Position.Y;
		var (focusX, focusY) = _cameraFocus.Advance(
			cosX, cosY,
			_cameraSmoothing,
			_lastFrameDelta,
			_cameraTeleportSnapTiles);
		var focus = new Vector3((float)focusX, 0, (float)focusY);
		_camera.Position = focus + CameraRigOffset;
		_camera.LookAt(focus, Vector3.Up);
		ApplyAoiZoomClampIfNeeded();
		_camera.Size = _cameraSize;
	}

	// AOI-EDGE ZOOM CLAMP: cap zoom-out so the AOI edge sits OFF-SCREEN. For an orthographic camera of Size S at
	// the rig's pitch (sinPitch = rig.Y / |rig|), the ground-plane footprint half-extents are S·aspect/2 across
	// (screen-x maps 1:1 to ground) and (S/2)/sinPitch up the screen (foreshortening), so the worst case — a
	// screen CORNER — sees ground distance S·√((aspect/2)² + (0.5/sinPitch)²). Solve that ≤ radius − margin for
	// the max Size. Runs once per hello radius (and re-runs live if the radius value changes); reads the live
	// viewport aspect so fullscreen/portrait layouts stay covered.
	private void ApplyAoiZoomClampIfNeeded()
	{
		if (_client?.Server is not { } server || server.InterestRadiusUnits == _appliedAoiZoomClampRadius)
		{
			return;
		}

		var viewport = GetViewport().GetVisibleRect().Size;
		var aspect = viewport.Y > 0f ? viewport.X / viewport.Y : 16f / 9f;
		var sinPitch = CameraRigOffset.Y / CameraRigOffset.Length();
		var cornerFactor = MathF.Sqrt(MathF.Pow(aspect / 2f, 2f) + MathF.Pow(0.5f / sinPitch, 2f));
		var usableRadius = server.InterestRadiusUnits - AoiEdgeHideMarginUnits;
		var maxSize = usableRadius > 0f ? usableRadius / cornerFactor : _cameraSizeMin;

		_cameraSizeMax = Mathf.Max(_cameraSizeMin, Mathf.Max(maxSize, UserZoomOutFloor));
		if (_cameraSize > _cameraSizeMax)
		{
			_cameraSize = _cameraSizeMax;
		}

		_appliedAoiZoomClampRadius = server.InterestRadiusUnits;
		GD.Print($"AOI zoom clamp: radius={server.InterestRadiusUnits:0.#}u, aspect={aspect:0.##}, maxCameraSize={_cameraSizeMax:0.#} (corner stays ≥{AoiEdgeHideMarginUnits:0.#}u inside the AOI edge).");
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
					: $"server: v{_client.Server.ProtocolVersion}, tick={_client.Server.TickRate}Hz, step={_client.Server.StepCooldownMs}ms, aoi={_client.Server.InterestRadiusUnits:0.#}";
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
		// ECOLOGY E4: feed the minimap's region shading from the client's replicated ecology region set — read-only,
		// rebuilt in place each refresh (a handful of authored regions; no per-frame allocation churn worth avoiding
		// further than that).
		RefreshMinimapRegions();

		_hud.SetState(_hudState);
	}

	// S110: project the client's known Resource-kind entities onto HudState.MinimapObjects. Resources are point
	// entities (one tile position) in the protocol — there is no replicated collision footprint — so the on-map
	// square side is derived per-kind from a presentation constant. The minimap scales these by its live zoom.
	// Read-only: touches no movement/snapshot/AOI state.
	//
	// NODE-FIELD N2/N3 (docs/node-field-design.md D3/D6): the ~188 tree/rock/plant resource entities this used
	// to also catch are gone (harvestable nodes are catalogue-only now, rendered by NodeFieldPainter, never
	// entities) — House/Portal are the only Resource-kind entities left, so this now only ever plots those.
	// Deliberately NOT extended to also plot the ~5,000-node field (D6's "no nameplates at field scale" applies
	// just as much to minimap clutter).
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
				(float)state.Position.X, (float)state.Position.Y, footprint));
		}
	}

	// ECOLOGY E4: project the client's replicated ecology region set (MmoClient.EcologyRegions) onto
	// HudState.MinimapRegions. Read-only: touches no movement/snapshot/AOI state, no ecology simulation — this is
	// a pure mirror of whatever RegionEcologyMessage last carried for each region.
	private void RefreshMinimapRegions()
	{
		_hudState.MinimapRegions.Clear();
		if (_client is null)
		{
			return;
		}

		foreach (var region in _client.EcologyRegions.Values)
		{
			var states = new List<EcologyPopulationState>(region.Types.Count);
			for (var i = 0; i < region.Types.Count; i++)
			{
				states.Add(region.Types[i].State);
			}

			var worst = EcologyLegibility.WorstOf(states);
			_hudState.MinimapRegions.Add(new Mmo.Client.Godot.UI.HudState.MinimapRegion(
				region.MinTileX, region.MinTileY, region.MaxTileX, region.MaxTileY, worst));
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
			// PLAYER-COLLISION-TOGGLE: reflect the current replicated flag in the checkbox on each open (another admin may
			// have flipped it). SetPressedNoSignal so seeding never fires the toggle handler (no spurious admin flip).
			_playerCollisionCheck?.SetPressedNoSignal(_client?.PlayerCollisionEnabled ?? true);
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
			SetField(_combatRadiusTiles, tuning.RadiusUnits);
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
		if (_monsterTypeDropdown is null || _monsterFieldRows is null
			|| _client?.MonsterTuning is not { } tuning || tuning.Types.Count == 0)
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

		// DATA-DRIVEN: build one row per replicated field (only when the field set changed) then seed each from its
		// Value. The server is authoritative — a broadcast after Apply re-seeds the post-clamp values here.
		var t = tuning.Types[_monsterSelectedTypeIndex];
		RebuildMonsterFieldRowsIfNeeded(t.Fields);
		for (var i = 0; i < t.Fields.Count && i < _monsterFieldEdits.Count; i++)
		{
			SetField(_monsterFieldEdits[i].Edit, t.Fields[i].Value);
		}

		_monsterPanelSeededVersion = _client.MonsterTuningVersion;
	}

	// DATA-DRIVEN: (re)build the per-field rows iff the selected type's field set (the ordered Keys) differs from what
	// is currently built — so switching type or a server-side knob add/remove rebuilds, but a plain value re-seed (the
	// common case) does not churn the nodes. Each row is a labelled LineEdit (label = caption + a Min..Max hint).
	private void RebuildMonsterFieldRowsIfNeeded(IReadOnlyList<MonsterTuningField> fields)
	{
		if (_monsterFieldRows is null)
		{
			return;
		}

		var matches = _monsterFieldEdits.Count == fields.Count;
		for (var i = 0; matches && i < fields.Count; i++)
		{
			matches = _monsterFieldEdits[i].Key == fields[i].Key;
		}

		if (matches)
		{
			return;
		}

		// Remove the old rows from the tree immediately (no visual duplicate) then free them.
		foreach (var child in _monsterFieldRows.GetChildren())
		{
			_monsterFieldRows.RemoveChild(child);
			child.QueueFree();
		}

		_monsterFieldEdits.Clear();
		foreach (var f in fields)
		{
			var label = f.IsInteger
				? $"{f.Label} [{f.Min.ToString("0", CultureInfo.InvariantCulture)}..{f.Max.ToString("0", CultureInfo.InvariantCulture)}]"
				: $"{f.Label} [{f.Min.ToString("0.###", CultureInfo.InvariantCulture)}..{f.Max.ToString("0.###", CultureInfo.InvariantCulture)}]";
			var edit = AddTuningField(_monsterFieldRows, label, OnMonsterApplyPressed);
			_monsterFieldEdits.Add((f.Key, edit));
		}
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
		var serverRadius = _client?.Server?.InterestRadiusUnits ?? 35f;
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
		SetField(_moveCameraSmoothing, _cameraSmoothing);
		// New (S102).
		SetField(_moveCameraTeleportSnapTiles, _cameraTeleportSnapTiles);
		// remote-interp-tighten Part A: seed the remote-buffer field with the override if one is pinned, else the
		// computed default in effect — so the knob shows the real value, not a blank or a raw multiplier.
		SetField(_moveRemoteInterpBufferMs,
			_client?.RemoteInterpolationBufferOverrideMs ?? _client?.EffectiveDefaultRemoteInterpolationBufferMs ?? 0d);
		// Move-speed multiplier: seed to 1.0 (= the current base ÷ default base). At first open — the only time this
		// runs — no base change has been applied yet, so the live base equals the default and the multiplier is 1.0.
		// (The client tracks only the pinned default base from ServerHello, not a replicated live global base.)
		SetField(_moveSpeedMultiplier, 1.0d);
		// Per-player /speed multiplier: seed to 1.0 (= no per-entity multiplier; the local player runs at the global
		// base until a value is applied). The client tracks no replicated per-entity SpeedMultiplier, so 1.0 on open.
		SetField(_movePlayerSpeedMultiplier, 1.0d);
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

		// DATA-DRIVEN: loop the dynamically-built rows and send each parseable field on its "<typeId>.<Key>" key.
		// Invalid/blank fields are skipped so a typo in one never blocks the others; the server clamps authoritatively.
		foreach (var (key, edit) in _monsterFieldEdits)
		{
			if (TryReadField(edit, out var value))
			{
				_client.SendAdminSetTuning($"{typeId}.{key}", value);
			}
		}

		ShowInteractFeedback($"Monster tuning sent ({typeId}).");
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

		// S95: camera follow smoothing [0,30 /s]. The next UpdateCamera reads the new value. (Continuous default 10.)
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

		// remote-interp-tighten Part A: the remote jitter buffer (ms). A negative value (or blank, which fails to
		// parse and is skipped) reverts to the computed default; >= 0 pins it. Clamped client-side to a debug range
		// (the client also clamps). Applied live to every remote interpolator — no restart.
		if (TryReadField(_moveRemoteInterpBufferMs, out var remoteBuffer))
		{
			_client.SetRemoteInterpolationBufferMs(remoteBuffer < 0d ? -1d : Mathf.Clamp((float)remoteBuffer, 0f, 2000f));
		}

		// Move speed × multiplier — scales the GLOBAL base move speed live. Sends continuous.baseMoveSpeed =
		// DEFAULT_BASE_SPEED × multiplier, where DEFAULT_BASE_SPEED = 1000 / ServerHello.StepCooldownMs (the pinned
		// base cadence, SPEED1). The server admin-gates + clamps (0.1..100) authoritatively. Ignore ≤0 / unparseable
		// input (would stop or reverse everything). Retunes the local player, the bots, and every other entity.
		if (TryReadField(_moveSpeedMultiplier, out var speedMultiplier) && speedMultiplier > 0d)
		{
			var defaultBaseSpeed = 1000d / (_client.Server?.StepCooldownMs ?? 150);
			_client.SendAdminSetTuning("continuous.baseMoveSpeed", defaultBaseSpeed * speedMultiplier);
		}

		// PER-PLAYER speed × multiplier — sets ONLY the local player's per-entity SpeedMultiplier live by sending the
		// /speed <multiplier> server command (the same per-entity path the removed dropdown used). It multiplies on top
		// of the global base, so the effective local speed = base × globalMult × playerMult. The server admin-gates +
		// clamps /speed authoritatively; ignore ≤0 / unparseable input (a non-positive multiplier would be rejected).
		if (TryReadField(_movePlayerSpeedMultiplier, out var playerSpeedMultiplier) && playerSpeedMultiplier > 0d)
		{
			_client.SendChat($"/speed {playerSpeedMultiplier.ToString("0.###", CultureInfo.InvariantCulture)}");
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

	private void SendHeldMovement(TimeSpan now, double frameDelta)
	{
		if (_client is null || !_client.IsLoggedIn)
		{
			return;
		}

		// Determine the desired intent. While the chat box has focus we force "stopped" so held keys
		// don't drive the avatar while typing. Priority: real keyboard input (WASD) > mouse hold-to-move
		// heading > injected (debug-channel) direction. A held WASD key overrides the mouse heading while it is down.
		var chatFocused = _chatInput?.HasFocus() == true;

		// CONTROLLER (2026-07-04): the left stick is TRUE ANALOG 360° — a raw unit WorldVector at any bearing
		// (orchestrator ruling on the 8-way-vs-analog fork: an octant-snapped stick reads as notchy; the server
		// already accepts arbitrary unit bearings via the mouse free-angle path). UNCONDITIONAL — not gated on
		// _freeAngleMovement (that checkbox is a MOUSE-mode toggle in the admin-only F1 panel; a controller
		// player would never see it). Read UNGATED by chat focus (unlike the plain keyboard fallback below) — a
		// stick tilt can't type text, so it keeps steering even while the chat box has focus. When active it
		// occupies the SAME priority tier as WASD (replaces the keyboard vector for the frame; the mouse block
		// and the injected fallback below gate on it exactly as they gate on `keyboard`). Centered stick -> the
		// ordinary chat-gated keyboard read, unchanged.
		var controllerFree = CurrentControllerMoveHeading();
		var keyboard = controllerFree.HasValue || chatFocused ? null : CurrentDirection();

		// Resolve the MOUSE hold-to-walk contribution. 8-dir mode (default): the nearest-of-8 octant (Direction8),
		// exactly as before. FREE-ANGLE mode: the RAW player->cursor unit heading (any angle). Only one is active
		// per mode; both carry the same right-button/dead-zone/side-effect semantics (CurrentFreeAngleMouseHeading
		// mirrors CurrentMouseHeading). WASD (keyboard) is unaffected by the mode — it is inherently 8-way.
		Direction8? mouseDir = null;   // 8-dir mode: the octant heading
		WorldVector? mouseFree = null; // free-angle mode: the raw unit heading
		if (!controllerFree.HasValue && !keyboard.HasValue && !chatFocused)
		{
			if (_freeAngleMovement)
			{
				mouseFree = CurrentFreeAngleMouseHeading();
			}
			else
			{
				mouseDir = CurrentMouseHeading();
			}
		}

		var mouseActive = mouseDir.HasValue || mouseFree.HasValue;
		var injected = controllerFree.HasValue || keyboard.HasValue || mouseActive || chatFocused
			? null
			: CurrentInjectedDirection();

		// The heading the client SENDS + the predictor consumes: a unit WorldVector (null = stopped). Priority
		// controller stick / keyboard (one tier — the stick replaces WASD when active) > mouse > injected. In 8-dir
		// mode every source is a Direction8 whose ToUnitVector() is byte-identical to the pre-free-angle code
		// (keyboard ?? mouseDir ?? injected -> .ToUnitVector()). The RAW off-octant vectors are the stick (always)
		// and the mouse (free-angle mode only); WASD/injected stay 8-way. `headingOctant` is the nearest Direction8
		// of that heading — it drives the 8-way sprite facing / HUD (facing stays 8-way in both modes by design);
		// for an 8-dir source it IS the source octant, so 8-dir facing is unchanged.
		WorldVector? heading;
		Direction8? headingOctant;
		if (controllerFree.HasValue)
		{
			heading = controllerFree.Value;
			headingOctant = CursorHeading.NearestDirection8(controllerFree.Value.X, controllerFree.Value.Y);
		}
		else if (keyboard.HasValue)
		{
			heading = keyboard.Value.ToUnitVector();
			headingOctant = keyboard.Value;
		}
		else if (mouseFree.HasValue)
		{
			heading = mouseFree.Value;
			headingOctant = CursorHeading.NearestDirection8(mouseFree.Value.X, mouseFree.Value.Y);
		}
		else if (mouseDir.HasValue)
		{
			heading = mouseDir.Value.ToUnitVector();
			headingOctant = mouseDir.Value;
		}
		else if (injected.HasValue)
		{
			heading = injected.Value.ToUnitVector();
			headingOctant = injected.Value;
		}
		else
		{
			heading = null;
			headingOctant = null;
		}

		var moving = heading.HasValue;

		// CONTINUOUS MIGRATION (Phase 4): predict + send ONE per-input continuous MoveIntent PER RENDER FRAME — the RAW
		// direction (the held Direction8's unit world vector, or (0,0) when stopped) and THIS frame's dt. The predictor
		// integrates the predicted present immediately (zero latency) and MINTS the seq we stamp on the wire (sent ==
		// buffered). The server integrates each fresh input by its dt (its anti-speedhack budget caps the integrated
		// distance to real time). The per-frame model is self-redundant: a dropped frame is superseded by the next, so
		// no on-change/keepalive/stop-tail scheduling is needed. dt is clamped to a sane frame duration so a long Godot
		// hitch can't request a huge integrate (PredictAndBuffer + the server both re-clamp regardless).
		var dt = (float)Math.Clamp(frameDelta, 0d, 0.25d);

		// CONTINUOUS MIGRATION (Phase 4): advance the predictor's cosmetic render catch-up exactly ONCE per frame
		// (decays the post-reconcile correction offset). Called here (the single per-frame movement path) for both the
		// moving and stopped cases; no-op when no predictor is attached.
		_client.AdvanceRender(dt);

		if (moving)
		{
			var unit = heading!.Value;
			_client.PredictAndSendMove((float)unit.X, (float)unit.Y, dt);
			_lastSentMoving = true;
			_lastSentDirection = headingOctant!.Value;
		}
		else
		{
			// Stopped: send a (0,0) input each frame so the server's last-integrated input is a stop (it acks the
			// seq and zeroes velocity). Cheap; the server dedups by seq and the budget makes this harmless.
			_client.PredictAndSendMove(0f, 0f, dt);
			_lastSentMoving = false;
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

	// Called each frame from _Process after SampleRenderStates. While a trace is armed, append the target entity's
	// current render + authoritative position; when the window elapses, compute the smoothness result and disarm.
	private void RecordRenderTraceFrame()
	{
		if (!_renderTraceActive)
		{
			return;
		}

		if (_elapsedSeconds >= _renderTraceEndsAtSeconds)
		{
			_renderTraceResult = ComputeRenderTraceResult();
			_renderTraceActive = false;
			return;
		}

		foreach (var state in _renderStates)
		{
			if (state.NetworkId == _renderTraceNetworkId)
			{
				_renderTraceSamples.Add((_elapsedSeconds, state.Position.X, state.Position.Y,
					state.AuthoritativePosition.X, state.AuthoritativePosition.Y));
				break;
			}
		}
	}

	// Per-frame smoothness metrics from the captured samples: speed jitter (stddev of per-frame speed), max jerk
	// (largest frame-to-frame velocity change), direction reversals (>90° velocity flips — a jitter tell), and the
	// render-vs-authoritative offset (interp lag), plus a downsampled render path. Velocities are normalised by dt.
	private RenderTraceStatus ComputeRenderTraceResult()
	{
		var n = _renderTraceSamples.Count;
		if (n < 3)
		{
			return new RenderTraceStatus(false, true, _renderTraceNetworkId, n, 0d, 0d, 0d, 0d, 0, 0d, 0d,
				System.Array.Empty<double>(), System.Array.Empty<double>());
		}

		double sumSpeed = 0d, sumSpeedSq = 0d, maxJerk = 0d, sumOffset = 0d, maxOffset = 0d;
		var steps = 0;
		var reversals = 0;
		double prevVx = 0d, prevVy = 0d;
		var havePrevV = false;
		for (var i = 1; i < n; i++)
		{
			var a = _renderTraceSamples[i - 1];
			var b = _renderTraceSamples[i];
			var dt = b.T - a.T;
			if (dt <= 1e-6d)
			{
				continue;
			}

			var vx = (b.Rx - a.Rx) / dt;
			var vy = (b.Ry - a.Ry) / dt;
			var speed = System.Math.Sqrt((vx * vx) + (vy * vy));
			sumSpeed += speed;
			sumSpeedSq += speed * speed;
			steps++;
			if (havePrevV)
			{
				var jerk = System.Math.Sqrt(((vx - prevVx) * (vx - prevVx)) + ((vy - prevVy) * (vy - prevVy)));
				if (jerk > maxJerk)
				{
					maxJerk = jerk;
				}

				// A backward velocity flip (>90°) at a non-trivial speed — smooth travel never reverses frame-to-frame.
				if (((vx * prevVx) + (vy * prevVy)) < 0d && speed > 0.05d)
				{
					reversals++;
				}
			}

			prevVx = vx;
			prevVy = vy;
			havePrevV = true;
		}

		foreach (var s in _renderTraceSamples)
		{
			var off = System.Math.Sqrt(((s.Rx - s.Ax) * (s.Rx - s.Ax)) + ((s.Ry - s.Ay) * (s.Ry - s.Ay)));
			sumOffset += off;
			if (off > maxOffset)
			{
				maxOffset = off;
			}
		}

		var meanSpeed = steps > 0 ? sumSpeed / steps : 0d;
		var variance = steps > 0 ? System.Math.Max(0d, (sumSpeedSq / steps) - (meanSpeed * meanSpeed)) : 0d;
		var speedStdDev = System.Math.Sqrt(variance);
		var durationMs = (_renderTraceSamples[n - 1].T - _renderTraceSamples[0].T) * 1000d;

		// Downsample the render path to ~16 points for a compact trajectory readout.
		const int maxPoints = 16;
		var stride = System.Math.Max(1, n / maxPoints);
		var points = (n + stride - 1) / stride;
		var sampleX = new double[points];
		var sampleY = new double[points];
		var idx = 0;
		for (var i = 0; i < n && idx < points; i += stride)
		{
			sampleX[idx] = _renderTraceSamples[i].Rx;
			sampleY[idx] = _renderTraceSamples[i].Ry;
			idx++;
		}

		return new RenderTraceStatus(false, true, _renderTraceNetworkId, n, durationMs, meanSpeed, speedStdDev,
			maxJerk, reversals, n > 0 ? sumOffset / n : 0d, maxOffset, sampleX, sampleY);
	}

	void IControlHost.StartRenderTrace(uint networkId, double durationMs)
	{
		_renderTraceNetworkId = networkId;
		_renderTraceEndsAtSeconds = _elapsedSeconds + (System.Math.Clamp(durationMs, 100d, 15000d) / 1000d);
		_renderTraceSamples.Clear();
		_renderTraceResult = null;
		_renderTraceActive = true;
	}

	RenderTraceStatus IControlHost.ReadRenderTrace()
	{
		if (_renderTraceActive)
		{
			return new RenderTraceStatus(true, false, _renderTraceNetworkId, _renderTraceSamples.Count, 0d, 0d, 0d,
				0d, 0, 0d, 0d, System.Array.Empty<double>(), System.Array.Empty<double>());
		}

		return _renderTraceResult ?? new RenderTraceStatus(false, false, 0u, 0, 0d, 0d, 0d, 0d, 0, 0d, 0d,
			System.Array.Empty<double>(), System.Array.Empty<double>());
	}

	void IControlHost.SetRemoteInterpolationBuffer(double bufferMs)
	{
		_client?.SetRemoteInterpolationBufferMs(bufferMs);
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

		// CONTINUOUS MIGRATION (Phase 4): the tile-predictor frame-diag (LocalPredictorFrameDiagnostics: predicted-tile
		// X/Y, step-seq, reconcile Matched/Corrected/Snapped tallies, cadence) was deleted with LocalPlayerPredictor —
		// the continuous predictor has no step-seq / tile / reconcile-outcome counters. These CSV columns are kept
		// (stable schema for existing capture tooling) but always blank now. A continuous-predictor frame-diag
		// (PredictedX/Y, BufferedInputCount, LastCorrectionUnits, RenderVsPredictedUnits, Speed) can be wired here in a
		// followup if the render-velocity capture needs it.
		string predX = string.Empty, predY = string.Empty, stepSeq = string.Empty,
			recMatched = string.Empty, recCorrected = string.Empty, recSnapped = string.Empty, cadenceMs = string.Empty;

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

	// CONTROLLER (2026-07-04): the left-stick counterpart to CurrentDirection() above — but TRUE ANALOG: the
	// stick's raw bearing becomes a unit WorldVector at ANY angle (the free-angle mouse heading's shape), not an
	// octant. The stick axes are SCREEN-relative (up on the stick = up on screen), the same frame WASD's x/y
	// axes live in, and the camera is fixed isometric — so the axes go through the SAME 45° screen->world
	// rotation ScreenRelativeDirectionMapper.FromInputAxes encodes, just continuously instead of via the 8-entry
	// sign table: worldDx = x + y, worldDz = y - x (verified against all 8 FromInputAxes rows: each sign combo's
	// rotated vector normalizes to exactly that row's Direction8 unit vector — the stick pushed to a WASD corner
	// moves identically to holding those keys, and every bearing in between is now reachable). Normalized to
	// length 1 (speed is fixed server-side; only direction is analog). Called from SendHeldMovement UNGATED by
	// chat focus — a stick tilt can't type text, so it keeps steering even while the chat box has focus;
	// SendHeldMovement falls back to the (chat-gated) keyboard read only when this is null (centered stick).
	private WorldVector? CurrentControllerMoveHeading()
	{
		if (!TryGetJoyAxisVector(JoyAxis.LeftX, JoyAxis.LeftY, out var stickX, out var stickY))
		{
			return null;
		}

		var worldDx = stickX + stickY;
		var worldDz = stickY - stickX;
		return new WorldVector(worldDx, worldDz).Normalized();
	}

	// CONTROLLER (2026-07-04): the first connected joypad's raw value for a single axis, or 0 if none is
	// connected. Re-queries GetConnectedJoypads() every call — no cached device id, so a mid-session hot-plug
	// (Godot/XInput handles the detection natively on Windows) is picked up on the very next poll.
	private static float GetFirstJoyAxis(JoyAxis axis)
	{
		var joypads = Input.GetConnectedJoypads();
		return joypads.Count == 0 ? 0f : Input.GetJoyAxis(joypads[0], axis);
	}

	// CONTROLLER (2026-07-04): shared by the left stick (movement, above) and the right stick (aim,
	// PollControllerAim) — reads both axes of the first connected joypad and applies the shared RADIAL deadzone
	// (compared on vector length, not per-axis, so the dead zone is circular rather than a smaller effective
	// square). Returns false — and leaves x/y at 0 — when no joypad is connected or the pair sits within the
	// deadzone.
	// LIVE FIX (2026-07-05, user repro: "it's moving both players at the same time"): unlike keyboard/mouse (which
	// the OS routes to the FOCUSED window only), Godot polls joypads GLOBALLY — two clients on one machine both
	// obey the same pad. ALL controller input is therefore focus-gated: this method (both sticks — movement + aim),
	// PollControllerTriggers (RT/LT), and the InputEventJoypadButton branch in _UnhandledInput each require
	// GetWindow().HasFocus(). Instance method (not static) for exactly that reason.
	private bool TryGetJoyAxisVector(JoyAxis axisX, JoyAxis axisY, out float x, out float y)
	{
		if (!GetWindow().HasFocus())
		{
			x = 0f;
			y = 0f;
			return false;
		}

		x = GetFirstJoyAxis(axisX);
		y = GetFirstJoyAxis(axisY);
		if ((x * x) + (y * y) < ControllerStickDeadzone * ControllerStickDeadzone)
		{
			x = 0f;
			y = 0f;
			return false;
		}

		return true;
	}

	private static Vector3 TileToWorld(TileCoord tile, float y = 0f)
	{
		return new Vector3(tile.X, y, tile.Y);
	}

	// LIVING-ENEMIES P2-POLISH: the shared flat quad + RED material for the monster-home markers. Full-tile (0.96) so
	// a home reads as "this tile", red + semi-transparent + unshaded so it sits flat and legible over any terrain.
	private static readonly PlaneMesh MonsterHomeMarkerMesh = new() { Size = new Vector2(0.96f, 0.96f) };
	private static readonly StandardMaterial3D MonsterHomeMarkerMaterial = MarkerMaterial(new Color(0.90f, 0.12f, 0.12f, 0.55f));

	// DEBUG-SERVER-POSITIONS: the shared flat quad + CYAN material for the remote-entity server-tile markers.
	// A deliberately DISTINCT colour from the spawner anchors (red) so the server tile reads as its own thing;
	// slightly inset (0.85) so it stays legible inside the entity's body.
	private static readonly PlaneMesh ServerPositionMarkerMesh = new() { Size = new Vector2(0.85f, 0.85f) };
	private static readonly StandardMaterial3D ServerPositionMarkerMaterial = MarkerMaterial(new Color(0.10f, 0.85f, 0.95f, 0.55f));

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

	// DEBUG-SERVER-POSITIONS: sync the CYAN server-position markers to every entity's continuous authoritative
	// position each frame. Mirrors UpdateMonsterHomeMarkers (per-id pooled flat markers) but keyed by network id and
	// driven by _renderStates (refreshed in SampleRenderStates) instead of SpawnerMarkers. A marker is created when an
	// entity is first seen and freed when its entity despawns / leaves AOI or the toggle is off. Unlike the fixed
	// spawner anchors, the marker is RE-POSITIONED every frame: it tracks the continuous AuthoritativePosition (the
	// confirmed server position) while the body smooths toward it, so under movement the marker visibly LEADS the body
	// — that gap IS the interpolation lag the human wants to see. The LOCAL player IS included: its body renders the
	// PREDICTED position, so its marker shows the prediction-vs-server gap. No-op until the world root exists.
	private void UpdateServerPositionMarkers()
	{
		if (_worldRoot is null)
		{
			return;
		}

		// F1 Visual "Server positions" toggle (default OFF). When off, free any shown markers and skip — the cyan
		// markers only appear when the toggle is on.
		if (!_showServerPositions)
		{
			if (_serverPositionMarkers.Count > 0)
			{
				foreach (var marker in _serverPositionMarkers.Values)
				{
					marker.QueueFree();
				}

				_serverPositionMarkers.Clear();
			}

			return;
		}

		// Add/position a marker per entity at its continuous authoritative server position; track which ids we saw this
		// frame so departed entities can be freed below. The LOCAL player is INCLUDED (was excluded): its body renders
		// the PREDICTED position, so its cyan marker — painted at the continuous AuthoritativePosition (the confirmed
		// server position) — is exactly the prediction-vs-server gap the user wants to SEE.
		_serverPositionSeenScratch.Clear();
		foreach (var state in _renderStates)
		{
			_serverPositionSeenScratch.Add(state.NetworkId);
			if (!_serverPositionMarkers.TryGetValue(state.NetworkId, out var marker))
			{
				marker = new MeshInstance3D
				{
					Name = $"ServerPos_{state.NetworkId}",
					Mesh = ServerPositionMarkerMesh,
					MaterialOverride = ServerPositionMarkerMaterial,
				};
				_worldRoot.AddChild(marker);
				_serverPositionMarkers[state.NetworkId] = marker;
			}

			// CONTINUOUS: position the marker from the true continuous AuthoritativePosition (the confirmed server
			// WorldVector), NOT the rounded AuthoritativeTile — so under movement it tracks the server position SMOOTHLY
			// instead of snapping/teleporting tile-to-tile (that grid-snap read as jank). A hair above the spawner
			// anchors (0.03) so it wins the overlap z-fight and stays readable on top.
			marker.Position = new Vector3((float)state.AuthoritativePosition.X, 0.06f, (float)state.AuthoritativePosition.Y);
		}

		// Drop markers whose entity is gone this frame (despawned / left AOI).
		if (_serverPositionMarkers.Count > 0)
		{
			_serverPositionStaleScratch.Clear();
			foreach (var id in _serverPositionMarkers.Keys)
			{
				if (!_serverPositionSeenScratch.Contains(id))
				{
					_serverPositionStaleScratch.Add(id);
				}
			}

			foreach (var id in _serverPositionStaleScratch)
			{
				_serverPositionMarkers[id].QueueFree();
				_serverPositionMarkers.Remove(id);
			}
		}
	}

	// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the ground-telegraph decals — a flat disc pair per active
	// telegraph, ALWAYS ON (this is pillar-2 gameplay legibility like the aim wedge, not a debug overlay like the
	// spawner/server-position markers). The ZONE disc is the full danger area at the EXACT wire radius; the FILL disc
	// grows from the centre with the deadline-form progress and reaches the zone edge precisely at resolve tick T; on
	// resolve the fill flashes bright for the core's brief flash window, then the entry is pruned and the nodes free.
	//
	// HONEST TELEGRAPH (user decision, 2026-07-03): the drawn circle IS the hit rule. The zone disc is scaled to
	// exactly the replicated radius — no padding, shrink, or edge bias — because membership is deliberately
	// CENTER-POINT (a player is hit iff its centre is inside; a body clipping the rim does NOT count, divergent from
	// melee body-clip by decision), so the true edge must stay crisp and truthful. The unit disc is a flattened
	// cylinder (Godot has no disc primitive); scaling it by (radius, 1, radius) puts the mesh edge AT the radius.
	private static readonly CylinderMesh TelegraphDiscMesh = new() { TopRadius = 1f, BottomRadius = 1f, Height = 0.02f, RadialSegments = 48 };
	private static readonly StandardMaterial3D TelegraphZoneMaterial = MarkerMaterial(new Color(1.00f, 0.35f, 0.10f, 0.24f));
	private static readonly StandardMaterial3D TelegraphFillMaterial = MarkerMaterial(new Color(0.95f, 0.16f, 0.08f, 0.45f));
	private static readonly StandardMaterial3D TelegraphFlashMaterial = MarkerMaterial(new Color(1.00f, 0.93f, 0.65f, 0.85f));

	// The two pooled nodes of one decal (created together, freed together, keyed by telegraph id). Kind selects how the
	// FILL animates (circle/wedge scale uniformly from the origin; a line grows its LENGTH only) — see UpdateTelegraphDecals.
	private sealed record TelegraphDecalNodes(MeshInstance3D Zone, MeshInstance3D Fill, TelegraphShapeKind Kind);

	private readonly Dictionary<ulong, TelegraphDecalNodes> _telegraphDecals = [];
	private readonly List<TelegraphDecalState> _telegraphDecalScratch = [];
	private readonly List<ulong> _telegraphDecalStaleScratch = [];

	// Sync the decals to the client's active-telegraph projection each frame (mirrors UpdateMonsterHomeMarkers'
	// pooled create/position/free shape, keyed by telegraph id). CopyTelegraphDecalsTo computes the fill progress off
	// the cosmetic server clock and prunes entries whose resolve flash has passed, so "gone from the list" IS the
	// despawn signal — there is no telegraph-end message (clients self-resolve at the shared deadline T). Cheap: the
	// active set is tiny (telegraphs live ~1.5 s) and empty almost always. No-op until the world root exists.
	private void UpdateTelegraphDecals()
	{
		if (_worldRoot is null || _client is null)
		{
			return;
		}

		_client.CopyTelegraphDecalsTo(_telegraphDecalScratch);

		foreach (var decal in _telegraphDecalScratch)
		{
			if (!_telegraphDecals.TryGetValue(decal.TelegraphId, out var nodes))
			{
				// WEDGE+LINE (S-telegraph-shapes-wedge-line): the decal mesh is built PER KIND from the LOCKED wire
				// shape (drawn == hit test). A circle reuses the shared unit disc scaled by radius; a wedge/line bakes
				// its exact geometry into a per-telegraph ArrayMesh (apex/edge at the origin, authored along +X) and is
				// yawed to the aim bearing — so the fill can grow the reach (wedge) or the length (line) by scaling.
				var mesh = BuildTelegraphMesh(decal);
				nodes = new TelegraphDecalNodes(
					new MeshInstance3D
					{
						Name = $"TelegraphZone_{decal.TelegraphId}",
						Mesh = mesh,
						MaterialOverride = TelegraphZoneMaterial,
					},
					new MeshInstance3D
					{
						Name = $"TelegraphFill_{decal.TelegraphId}",
						Mesh = mesh,
						MaterialOverride = TelegraphFillMaterial,
					},
					decal.Kind);
				_worldRoot.AddChild(nodes.Zone);
				_worldRoot.AddChild(nodes.Fill);
				_telegraphDecals[decal.TelegraphId] = nodes;

				// The origin/aim are LOCKED at cast and the zone extent never changes — position + rotation + the
				// STATIC zone scale are set once on create. Heights: zone at 0.02, fill a hair above (0.035) so the
				// growing fill always wins their z-overlap; both sit under the debug server-position markers (0.06) so
				// diagnostics stay readable on top. A +Y yaw of -aim maps the mesh's authored +X to the world bearing
				// (the FlashAimWedge convention); circle is rotation-agnostic (yaw 0 is harmless).
				var yaw = new Vector3(0f, -(float)decal.AimRadians, 0f);
				nodes.Zone.Position = new Vector3((float)decal.Origin.X, 0.02f, (float)decal.Origin.Y);
				nodes.Zone.Rotation = yaw;
				nodes.Fill.Position = new Vector3((float)decal.Origin.X, 0.035f, (float)decal.Origin.Y);
				nodes.Fill.Rotation = yaw;
				// Circle bakes a UNIT disc (scaled by radius); wedge/line bake their true size (scaled by 1). Set the
				// zone's static scale accordingly.
				nodes.Zone.Scale = decal.Kind == TelegraphShapeKind.Circle
					? new Vector3((float)decal.Radius, 1f, (float)decal.Radius)
					: Vector3.One;
			}

			// The fill is the per-frame animated half: it grows to the TRUE edge exactly at T, then the resolve flash.
			// Circle/wedge grow UNIFORMLY from the origin (the disc radius / the wedge reach); a line grows its LENGTH
			// only (from the origin edge along the bearing), its width fixed. A tiny floor keeps the scale non-singular
			// at progress 0 without ever reading as a visible shape.
			var progress = decal.Resolved ? 1f : Mathf.Max((float)decal.Progress, 0f);
			if (decal.Resolved)
			{
				nodes.Fill.MaterialOverride = TelegraphFlashMaterial;
			}

			nodes.Fill.Scale = decal.Kind switch
			{
				// Line: grow x (length) only; y/z fixed so the corridor width stays true throughout the windup.
				TelegraphShapeKind.Line => new Vector3(Mathf.Max(progress, 0.001f), 1f, 1f),
				// Wedge: uniform reach growth (baked at true size → scale is the progress fraction).
				TelegraphShapeKind.Wedge => new Vector3(Mathf.Max(progress, 0.001f), 1f, Mathf.Max(progress, 0.001f)),
				// Circle: baked as a UNIT disc → scale by radius × progress (the historical path, unchanged).
				_ => new Vector3(Mathf.Max((float)(decal.Radius * progress), 0.01f), 1f, Mathf.Max((float)(decal.Radius * progress), 0.01f)),
			};
		}

		// Free decals whose telegraph left the projection (flash window over — the core pruned it).
		if (_telegraphDecals.Count > 0)
		{
			_telegraphDecalStaleScratch.Clear();
			foreach (var id in _telegraphDecals.Keys)
			{
				var seen = false;
				foreach (var decal in _telegraphDecalScratch)
				{
					if (decal.TelegraphId == id)
					{
						seen = true;
						break;
					}
				}

				if (!seen)
				{
					_telegraphDecalStaleScratch.Add(id);
				}
			}

			foreach (var id in _telegraphDecalStaleScratch)
			{
				var nodes = _telegraphDecals[id];
				nodes.Zone.QueueFree();
				nodes.Fill.QueueFree();
				_telegraphDecals.Remove(id);
			}
		}
	}

	// WEDGE+LINE (S-telegraph-shapes-wedge-line): build the decal mesh for one telegraph's LOCKED wire shape. Circle
	// reuses the shared unit disc (scaled by radius at the call site); wedge/line bake their EXACT geometry authored in
	// the XZ plane pointing along +X (apex/near-edge at the local origin), so yawing the MeshInstance by -aim maps +X to
	// the world bearing — the drawn shape is the resolve shape, no padding/shrink (the honest-telegraph rule).
	private static Mesh BuildTelegraphMesh(TelegraphDecalState decal) => decal.Kind switch
	{
		TelegraphShapeKind.Wedge => BuildTelegraphWedgeMesh((float)decal.Radius, (float)decal.HalfAngleRadians),
		TelegraphShapeKind.Line => BuildTelegraphLineMesh((float)decal.Radius, (float)decal.HalfWidth),
		_ => TelegraphDiscMesh,
	};

	// A flat pie-slice (apex at local origin, +X centreline) spanning [-halfAngle, +halfAngle] out to `radius`. A
	// triangle fan from the apex — the same construction as the free-aim wedge, but sized from the telegraph's own
	// half-angle/reach (radians in) rather than the combat tuning. The fill scales this uniformly to grow the reach.
	private static ArrayMesh BuildTelegraphWedgeMesh(float radius, float halfAngleRadians)
	{
		const int segments = 24;
		var verts = new Godot.Collections.Array();
		verts.Resize((int)Mesh.ArrayType.Max);

		var points = new System.Collections.Generic.List<Vector3>(segments + 2) { Vector3.Zero };
		for (var i = 0; i <= segments; i++)
		{
			var a = -halfAngleRadians + (2f * halfAngleRadians * i / segments);
			points.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
		}

		var vertexArray = new Vector3[segments * 3];
		var v = 0;
		for (var i = 1; i <= segments; i++)
		{
			// Wind so the triangle faces up (+Y); the material is double-sided anyway.
			vertexArray[v++] = points[0];
			vertexArray[v++] = points[i + 1];
			vertexArray[v++] = points[i];
		}

		verts[(int)Mesh.ArrayType.Vertex] = vertexArray;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, verts);
		return mesh;
	}

	// A flat oriented rectangle: x in [0, length] (the near edge at the local origin, extending along +X), z in
	// [-halfWidth, +halfWidth]. Two triangles. The fill scales local X (only) to grow the length toward the far edge.
	private static ArrayMesh BuildTelegraphLineMesh(float length, float halfWidth)
	{
		var verts = new Godot.Collections.Array();
		verts.Resize((int)Mesh.ArrayType.Max);

		var a = new Vector3(0f, 0f, -halfWidth);
		var b = new Vector3(length, 0f, -halfWidth);
		var c = new Vector3(length, 0f, halfWidth);
		var d = new Vector3(0f, 0f, halfWidth);
		// Two triangles (a,c,b) + (a,d,c), wound to face +Y (double-sided material regardless).
		var vertexArray = new[] { a, c, b, a, d, c };

		verts[(int)Mesh.ArrayType.Vertex] = vertexArray;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, verts);
		return mesh;
	}

	// FREEAIM FEEL KNOBS (client telegraph). COMBAT-TUNING: the half-angle/radius the wedge is drawn from are no
	// longer client constants — they MIRROR the server's REPLICATED CombatTuningSnapshot (combat.halfAngleDeg /
	// combat.radiusTiles), so the drawn wedge ALWAYS equals the server's real danger area (the earlier "keep these in
	// sync by hand" duplication is gone). The mesh is rebuilt whenever the replicated snapshot changes
	// (RebuildAimWedgeMeshIfNeeded, keyed off MmoClient.CombatTuningVersion). These defaults reproduce the historical
	// look before the first snapshot lands (45°, 1.6 tiles).
	private float _aimWedgeHalfAngleDegrees = 45f;
	private float _aimWedgeRadiusUnits = 1.6f;
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
	private ArrayMesh BuildAimWedgeMesh(float halfAngleDegrees, float radiusUnits)
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
			points.Add(new Vector3(Mathf.Cos(a) * radiusUnits, 0f, Mathf.Sin(a) * radiusUnits));
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
			_aimWedgeRadiusUnits = (float)tuning.RadiusUnits;
		}

		_aimWedgeMesh = BuildAimWedgeMesh(_aimWedgeHalfAngleDegrees, _aimWedgeRadiusUnits);
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

	// ---- DUO-SKILLSHOT (exp/duo-abilities): Q = fire the fusion skillshot (HOLD to aim, RELEASE to fire) ----

	private const Key SkillshotFireKey = Key.Q;

	// The faint intercept-preview lines (self + partner), pooled MeshInstance3D under _worldRoot with a shared thin-
	// quad ArrayMesh scaled to the projectile's max range and yawed to the aim heading.
	private MeshInstance3D? _selfPreviewLine;
	private MeshInstance3D? _partnerPreviewLine;
	private ArrayMesh? _previewLineMesh;

	// Cyan, faint, unshaded — reads as an aim guide over the terrain, distinct from the red attack wedge.
	private static readonly StandardMaterial3D SkillshotPreviewMaterial = MarkerMaterial(new Color(0.30f, 0.95f, 0.90f, 0.30f));

	// The preview line length = the server's projectile max range (SkillshotEngine.ProjectileMaxRangeUnits). Kept in
	// sync by hand (an experiment const, not replicated).
	private const float SkillshotPreviewRangeUnits = 14f;

	// ~8Hz preview relay cadence (only sent while a partner exists), matching the throttle the design specifies.
	private const ulong SkillshotPreviewSendIntervalMs = 125;

	private bool _skillshotAiming;
	private float _skillshotAimRadians;
	private ulong _lastPreviewSentMs;

	// CONTROLLER (2026-07-04): per-frame trigger read — LT/RT are AXES on XInput pads (0..1), not buttons, so
	// they can't arrive as InputEventJoypadButton and are instead polled here alongside the sticks. RT is an
	// EDGE (press-only) read: it mirrors the LMB _UnhandledInput handler exactly (same TryAttack() call; aim
	// comes from the seam, see TryGetAimWorldPoint), so a held trigger doesn't spam attacks every frame. LT is a
	// LEVEL (HELD) read — OR'd into UpdateSkillshotAim's own `held` computation below — because holding LT IS
	// the "aim the skillshot" gesture, mirroring Q; both ignore chat focus entirely (see the controller spec's
	// chat-focus rule).
	private void PollControllerTriggers()
	{
		// Focus gate (see TryGetJoyAxisVector): an unfocused window must not attack or hold a skillshot aim off a
		// pad another client instance owns. Clearing LT here while unfocused makes UpdateSkillshotAim's `held` drop;
		// its release branch treats the focus loss as a CANCEL, not a fire.
		if (!GetWindow().HasFocus())
		{
			_prevRightTriggerHeld = false;
			_controllerLeftTriggerHeld = false;
			return;
		}

		var rightTriggerHeld = GetFirstJoyAxis(JoyAxis.TriggerRight) >= ControllerTriggerThreshold;
		if (rightTriggerHeld && !_prevRightTriggerHeld)
		{
			TryAttack();
		}

		_prevRightTriggerHeld = rightTriggerHeld;
		_controllerLeftTriggerHeld = GetFirstJoyAxis(JoyAxis.TriggerLeft) >= ControllerTriggerThreshold;
	}

	// Per-frame: poll Q (hold-to-aim, release-to-fire). While held, resolve the player→cursor aim, throttle-relay it to
	// the partner (so they can line up an intercept), and draw the local preview line. On release, fire the skillshot
	// toward the last aim and tell the partner to stop drawing. Chat focus cancels an in-progress aim WITHOUT firing
	// (so opening chat mid-hold doesn't loose a shot).
	private void UpdateSkillshotAim(TimeSpan now)
	{
		if (_client is null)
		{
			return;
		}

		var chatFocused = _chatInput?.HasFocus() == true;

		// CONTROLLER (2026-07-04): LT (a HELD level, polled in PollControllerTriggers) is OR'd in and ignores
		// chat focus — a physical trigger can't type text. The keyboard term keeps its original chatFocused
		// guard untouched, so Q's behaviour is byte-identical to before this feature.
		var held = _client.IsLoggedIn && (_controllerLeftTriggerHeld || (!chatFocused && Input.IsKeyPressed(SkillshotFireKey)));

		if (held)
		{
			_skillshotAimRadians = TryGetAimToCursor(out var cursorAim) ? cursorAim : LocalFacingRadians();
			_skillshotAiming = true;

			// Relay the aim to the partner (only when paired), throttled ~8Hz.
			var nowMs = Time.GetTicksMsec();
			if (_client.IsPaired && nowMs - _lastPreviewSentMs >= SkillshotPreviewSendIntervalMs)
			{
				_client.SendAimPreview(AimAngle.Quantize(_skillshotAimRadians), true);
				_lastPreviewSentMs = nowMs;
			}
		}
		else if (_skillshotAiming)
		{
			_skillshotAiming = false;

			// Fire on RELEASE (not on a chat-focus cancel). A chat-focus steal leaves `held` false but `chatFocused`
			// true — treat that as a cancel so opening chat mid-hold doesn't fire. A WINDOW-focus loss mid-LT-hold
			// (alt-tab / the two-clients-one-pad case) is the same kind of steal — cancel, don't loose the shot.
			if (!chatFocused && GetWindow().HasFocus())
			{
				_client.SendFireSkillshot(AimAngle.Quantize(_skillshotAimRadians));
			}

			if (_client.IsPaired)
			{
				_client.SendAimPreview(AimAngle.Quantize(_skillshotAimRadians), false);
			}
		}

		UpdateSkillshotPreviewLines();
	}

	// Build the shared thin-quad line mesh once: a unit rectangle spanning X in [0,1], thin in Z, in the ground plane.
	// The MeshInstance3D scales X to the range and yaws by -aim, so +X maps to the world aim bearing (same convention
	// as the aim wedge).
	private void EnsureSkillshotPreviewLines()
	{
		if (_worldRoot is null || _selfPreviewLine is not null)
		{
			return;
		}

		if (_previewLineMesh is null)
		{
			const float halfWidth = 0.06f;
			var verts = new Godot.Collections.Array();
			verts.Resize((int)Mesh.ArrayType.Max);
			verts[(int)Mesh.ArrayType.Vertex] = new Vector3[]
			{
				new(0f, 0f, -halfWidth), new(1f, 0f, -halfWidth), new(1f, 0f, halfWidth),
				new(0f, 0f, -halfWidth), new(1f, 0f, halfWidth), new(0f, 0f, halfWidth),
			};
			_previewLineMesh = new ArrayMesh();
			_previewLineMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, verts);
		}

		_selfPreviewLine = new MeshInstance3D { Name = "SkillshotPreviewSelf", Mesh = _previewLineMesh, MaterialOverride = SkillshotPreviewMaterial, Visible = false };
		_partnerPreviewLine = new MeshInstance3D { Name = "SkillshotPreviewPartner", Mesh = _previewLineMesh, MaterialOverride = SkillshotPreviewMaterial, Visible = false };
		_worldRoot.AddChild(_selfPreviewLine);
		_worldRoot.AddChild(_partnerPreviewLine);
	}

	private void UpdateSkillshotPreviewLines()
	{
		EnsureSkillshotPreviewLines();
		if (_selfPreviewLine is null || _partnerPreviewLine is null)
		{
			return;
		}

		// Self line: while aiming, from the local player along the current aim.
		if (_skillshotAiming && TryGetLocalRenderPosition(out var sx, out var sz))
		{
			PlacePreviewLine(_selfPreviewLine, sx, sz, _skillshotAimRadians);
			_selfPreviewLine.Visible = true;
		}
		else
		{
			_selfPreviewLine.Visible = false;
		}

		// Partner line: while the partner is relaying an active aim, from the partner's position along their heading.
		if (_client?.PartnerAimPreview is { Active: true } partner
			&& TryGetRenderPosition(partner.ShooterNetworkId, out var px, out var pz))
		{
			PlacePreviewLine(_partnerPreviewLine, px, pz, partner.HeadingRadians);
			_partnerPreviewLine.Visible = true;
		}
		else
		{
			_partnerPreviewLine.Visible = false;
		}
	}

	// Position/orient/scale a preview line from a shooter's world XZ along `aimRadians` out to the projectile range.
	private static void PlacePreviewLine(MeshInstance3D line, float x, float z, float aimRadians)
	{
		line.Position = new Vector3(x, 0.06f, z);
		// Yaw +X -> world aim (θ = -aim), the same mapping the aim wedge uses.
		line.Rotation = new Vector3(0f, -aimRadians, 0f);
		line.Scale = new Vector3(SkillshotPreviewRangeUnits, 1f, 1f);
	}

	// ---- DUO-WAVE2 (exp/duo-abilities): abilities 2-4 presentation (shield bubble, tether beam, blast-charge decal) ----

	private static readonly SphereMesh ShieldBubbleMesh = new() { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 };
	private static readonly StandardMaterial3D ShieldBubbleMaterial = MarkerMaterial(new Color(0.45f, 0.80f, 1.00f, 0.22f));

	// Tether beam colours by the band the client recomputes from the two players' live distance (cool in the sweet
	// spot, amber in the warning gap, red overstretched/broken) — the honest tension read.
	private static readonly StandardMaterial3D TetherSweetMaterial = MarkerMaterial(new Color(0.25f, 0.60f, 1.00f, 0.55f));
	private static readonly StandardMaterial3D TetherWarningMaterial = MarkerMaterial(new Color(1.00f, 0.65f, 0.10f, 0.60f));
	private static readonly StandardMaterial3D TetherOverstretchMaterial = MarkerMaterial(new Color(1.00f, 0.15f, 0.10f, 0.70f));

	// The blast-charge decal reuses the telegraph disc mesh; a distinct magenta so it never reads as an enemy telegraph.
	private static readonly StandardMaterial3D MidpointChargeMaterial = MarkerMaterial(new Color(0.90f, 0.30f, 1.00f, 0.38f));

	private readonly Dictionary<uint, MeshInstance3D> _shieldBubbles = [];
	private readonly List<uint> _shieldStaleScratch = [];
	private MeshInstance3D? _tetherBeam;
	private MeshInstance3D? _chargeDecal;

	// Per-frame: mirror MmoClient's replicated duo state into pooled world nodes. Pure presentation — the server owns
	// every effect; this only draws. Echo cues are drained (a future flash+ring hook; bounded in the core meanwhile).
	private void UpdateDuoVisuals()
	{
		if (_worldRoot is null || _client is null)
		{
			return;
		}

		// Drain echo cues (flash + expanding ring is a deferred polish hook — the core bounds the queue meanwhile).
		while (_client.TryDequeueEchoCue(out _))
		{
		}

		UpdateShieldBubbles();
		UpdateTetherBeam();
		UpdateChargeDecal();
	}

	// A translucent sphere around each shielded entity, scaled by shield strength (bigger bubble = stronger tier).
	private void UpdateShieldBubbles()
	{
		var shields = _client!.Shields;
		foreach (var (networkId, shield) in shields)
		{
			if (!TryGetRenderPosition(networkId, out var x, out var z))
			{
				continue;
			}

			if (!_shieldBubbles.TryGetValue(networkId, out var node))
			{
				node = new MeshInstance3D
				{
					Name = $"ShieldBubble_{networkId}",
					Mesh = ShieldBubbleMesh,
					MaterialOverride = ShieldBubbleMaterial,
				};
				_worldRoot!.AddChild(node);
				_shieldBubbles[networkId] = node;
			}

			// Radius scales gently with strength: solo(10) ~0.85, Good(25) ~1.05, Perfect(40) ~1.25 units.
			var radius = 0.75f + (Mathf.Min(shield.Strength, (ushort)60) / 60f * 0.7f);
			node.Position = new Vector3(x, 0.9f, z);
			node.Scale = new Vector3(radius, radius, radius);
			node.Visible = true;
		}

		// Free bubbles whose entity no longer carries a shield.
		if (_shieldBubbles.Count > 0)
		{
			_shieldStaleScratch.Clear();
			foreach (var id in _shieldBubbles.Keys)
			{
				if (!shields.ContainsKey(id))
				{
					_shieldStaleScratch.Add(id);
				}
			}

			foreach (var id in _shieldStaleScratch)
			{
				_shieldBubbles[id].QueueFree();
				_shieldBubbles.Remove(id);
			}
		}
	}

	// One stretched thin quad between the two linked players, coloured by the live distance band. Reuses the skillshot
	// preview line mesh (a unit rect along +X); positioned at the owner, yawed toward the partner, scaled to the gap.
	private void UpdateTetherBeam()
	{
		EnsureSkillshotPreviewLines();
		if (_previewLineMesh is null)
		{
			return;
		}

		if (_tetherBeam is null)
		{
			_tetherBeam = new MeshInstance3D { Name = "TetherBeam", Mesh = _previewLineMesh, MaterialOverride = TetherSweetMaterial, Visible = false };
			_worldRoot!.AddChild(_tetherBeam);
		}

		if (_client!.ActiveTether is not { } tether
			|| !TryGetRenderPosition(tether.OwnerNetworkId, out var ox, out var oz)
			|| !TryGetRenderPosition(tether.PartnerNetworkId, out var px, out var pz))
		{
			_tetherBeam.Visible = false;
			return;
		}

		var dx = px - ox;
		var dz = pz - oz;
		var distance = Mathf.Sqrt((dx * dx) + (dz * dz));
		var heading = Mathf.Atan2(dz, dx);
		_tetherBeam.Position = new Vector3(ox, 0.08f, oz);
		_tetherBeam.Rotation = new Vector3(0f, -heading, 0f);
		_tetherBeam.Scale = new Vector3(Mathf.Max(distance, 0.01f), 1f, 1f);
		_tetherBeam.MaterialOverride = tether.State == TetherState.Broken
			? TetherOverstretchMaterial
			: distance switch
			{
				>= 12f => TetherOverstretchMaterial,
				> 10f => TetherWarningMaterial,
				_ => TetherSweetMaterial,
			};
		_tetherBeam.Visible = true;
	}

	// The live-tracking blast-charge disc at the current midpoint (reuses the telegraph disc mesh, distinct colour).
	private void UpdateChargeDecal()
	{
		if (_chargeDecal is null)
		{
			_chargeDecal = new MeshInstance3D { Name = "MidpointCharge", Mesh = TelegraphDiscMesh, MaterialOverride = MidpointChargeMaterial, Visible = false };
			_worldRoot!.AddChild(_chargeDecal);
		}

		if (_client!.ActiveCharge is not { } charge)
		{
			_chargeDecal.Visible = false;
			return;
		}

		_chargeDecal.Position = new Vector3((float)charge.Origin.X, 0.04f, (float)charge.Origin.Y);
		var radius = Mathf.Max((float)charge.RadiusUnits, 0.05f);
		_chargeDecal.Scale = new Vector3(radius, 1f, radius);
		_chargeDecal.Visible = true;
	}

	// ---- CONTROLLER aim arrow (2026-07-05 feel-test): the aim-ownership cue ----

	// A small ground-level arrow at the local player pointing along the CONTINUOUS pad aim bearing (the raw
	// _controllerAimDirection, not the octant the facing snaps to). Visible ONLY while the controller owns aim
	// (_aimSourceIsController) and gone the moment mouse motion reclaims it — visible-in-pad-mode IS the toggle
	// (live-toggle discipline; no launch flag, no settings row). This intentionally closes the
	// "no aim-ownership cue" follow-up: alternating devices, the arrow is the tell for which one owns aim.
	// Kept deliberately small + translucent (a cue, not a skillshot telegraph — that's the cyan preview line).

	// Pale gold, faint, unshaded — reads on the dark ground and collides with none of the existing overlay
	// colours (cyan skillshot preview, red attack wedge, magenta blast charge, blue/amber/red tether).
	private static readonly StandardMaterial3D ControllerAimArrowMaterial = MarkerMaterial(new Color(1.00f, 0.85f, 0.35f, 0.45f));

	private MeshInstance3D? _controllerAimArrow;
	private ArrayMesh? _controllerAimArrowMesh;

	// Per-frame (cheap): lazily build the arrow node once (the tether-beam pattern), then only reposition/yaw and
	// flip Visible — no per-frame allocation. Anchored at the player's render position each frame so it tracks the
	// avatar exactly like the swing wedge does; yawed so the mesh's +X maps onto the world aim bearing (θ = -aim,
	// the same mapping PlacePreviewLine uses).
	private void UpdateControllerAimArrow()
	{
		if (_worldRoot is null)
		{
			return;
		}

		if (_controllerAimArrow is null)
		{
			// The arrow mesh, built ONCE at final size pointing +X in the ground plane: a thin shaft quad
			// (x 0.30→0.90, so it starts just outside the cat's body instead of under its feet) plus a head
			// triangle (x 0.90→1.15) — ~0.85u of visible arrow. Same raw-ArrayMesh idiom as the skillshot
			// preview line's unit quad.
			var vertices = new Godot.Collections.Array();
			vertices.Resize((int)Mesh.ArrayType.Max);
			vertices[(int)Mesh.ArrayType.Vertex] = new Vector3[]
			{
				// Shaft (two triangles).
				new(0.30f, 0f, -0.045f), new(0.90f, 0f, -0.045f), new(0.90f, 0f, 0.045f),
				new(0.30f, 0f, -0.045f), new(0.90f, 0f, 0.045f), new(0.30f, 0f, 0.045f),
				// Head (one triangle, wider than the shaft, tip at +X).
				new(0.90f, 0f, -0.13f), new(1.15f, 0f, 0f), new(0.90f, 0f, 0.13f),
			};
			_controllerAimArrowMesh = new ArrayMesh();
			_controllerAimArrowMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, vertices);

			_controllerAimArrow = new MeshInstance3D
			{
				Name = "ControllerAimArrow",
				Mesh = _controllerAimArrowMesh,
				MaterialOverride = ControllerAimArrowMaterial,
				Visible = false,
			};
			_worldRoot.AddChild(_controllerAimArrow);
		}

		if (!_aimSourceIsController || _client?.IsLoggedIn != true
			|| !TryGetLocalRenderPosition(out var px, out var pz))
		{
			_controllerAimArrow.Visible = false;
			return;
		}

		// World bearing of the raw aim vector (+X east, +Y south) — the continuous direction the seam projects
		// from, so the arrow and the synthetic aim point always agree.
		var bearing = Mathf.Atan2((float)_controllerAimDirection.Y, (float)_controllerAimDirection.X);
		_controllerAimArrow.Position = new Vector3(px, 0.07f, pz);
		_controllerAimArrow.Rotation = new Vector3(0f, -bearing, 0f);
		_controllerAimArrow.Visible = true;
	}

	// FREEAIM: continuous local facing. Render-only, local-only, NOT replicated — the local player's visual yaws
	// smoothly toward the cursor's ground point each frame so the avatar "looks where you aim". The server still
	// only knows the discrete movement facing; this is pure presentation layered over it. No-op when the cursor pick
	// fails or the local visual isn't spawned yet.
	//
	// CONTROLLER AIM-FACING (2026-07-05 feel-test): this is the narrowest seam where the LOCAL player's visual
	// facing is chosen (the SetContinuousYaw/ClearContinuousYaw override PlayerVisual.ApplyFacing prefers over the
	// discrete movement Facing), so the pad branch lives here. On pad, facing follows AIM only while ACTIVELY
	// aiming — right stick past its deadzone THIS frame (_controllerAimStickActive, NOT the persistent ownership
	// flag, which would pin facing to a stale aim while walking) OR the LT skillshot hold (you're aiming a shot;
	// the character faces it even if the stick momentarily recentres). The facing is the aim's OCTANT
	// (NearestDirection8 — the 8-way sprite convention, per the user's feel-test call), not the raw bearing.
	// Otherwise the override is dropped and facing falls back to the movement-derived octant exactly as a
	// no-aim mouse frame does. Purely cosmetic and local: nothing sent to the server changes; remote players
	// still see the movement-derived facing (accepted mismatch for this experiment).
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

		if (_aimSourceIsController)
		{
			if (_controllerAimStickActive || _controllerLeftTriggerHeld)
			{
				// CONTINUOUS pad aim facing (orchestrator revision of the octant-snap first cut): the mouse
				// FREEAIM path below yaws the model smoothly toward the raw cursor bearing, and the avatar is a
				// 3D model, not an 8-way sprite — the pad matches it. _controllerAimDirection is already the unit
				// (dx east, dz south) the mouse branch derives via cos/sin, so it feeds the identical
				// atan2(-dx,-dz) model-forward math (see the comment on the mouse branch below).
				visual.SetContinuousYaw(Mathf.Atan2(-(float)_controllerAimDirection.X, -(float)_controllerAimDirection.Y));
			}
			else
			{
				// Pad owns aim but isn't aiming right now: movement-derived facing, exactly as today.
				visual.ClearContinuousYaw();
			}

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

	// DIAG1: the LOCAL-player movement read-out, refreshed every overlay tick (~10 Hz). recv/s (snapshots applied per
	// second — is the server->client confirm channel alive?) is the only live field now.
	// CONTINUOUS MIGRATION (Phase 4): the tile-predictor recovery-chain fields (pred / conf / lead and the reconcile
	// Matched/Corrected/Snapped tallies) were retired with LocalPlayerPredictor — the continuous predictor has no
	// step-seq or tile-reconcile outcomes, so they were removed from MovementDebugSnapshot and this readout.
	private static string FormatRecoveryDiag(MovementDebugSnapshot d)
	{
		return $"DIAG recv/s={d.SnapshotsPerSecond:0.0}";
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
