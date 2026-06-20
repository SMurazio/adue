using System.Collections.Generic;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

// Seam between the transport-only DebugControlChannel and MmoClientRoot. The channel parses/serializes
// JSON and owns the socket; the host injects input through the same paths real input uses and reads
// live telemetry off the client. Keeping this an interface lets the channel stay free of Godot/client
// internals and keeps every behavior-affecting call on the main thread (the channel is polled from
// _Process, so all host calls already run on the render thread).
internal interface IControlHost
{
    // Commands — routed through the same client/input methods as real keyboard input.
    void BeginManualMove(Direction8 direction, double durationMs);

    void StopMovement();

    void SendChat(string text);

    void TogglePerfHud();

    void ToggleFullscreen();

    bool TryBeginAutopilot(string pattern, double durationMs, out string error);

    // Queries — read-only snapshots of live state.
    ControlTelemetry ReadTelemetry();

    MovementDebugSnapshot ReadMovementDebug();

    IReadOnlyList<EntityRenderState> ReadEntities();

    ControlState ReadState();
}

internal readonly record struct ControlTelemetry(
    double Fps,
    double FrameMsLast,
    double FrameMsMax,
    double PollMsLast,
    double RenderStateMsLast,
    double EntitiesMsLast,
    double CameraMsLast,
    double OverlayMsLast,
    double PollMsMax,
    double RenderStateMsMax,
    double EntitiesMsMax,
    double CameraMsMax,
    double OverlayMsMax,
    long Gc0,
    long Gc1,
    long Gc2,
    long HitchCount,
    double MaxDivergence,
    long SnapCount,
    double CurrentSpeed);

internal readonly record struct ControlState(
    string Connection,
    bool LoggedIn,
    string Role,
    string Zone,
    int VisibleEntities,
    string LocalTile);
