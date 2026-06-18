using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

// Localhost-only debug control + telemetry channel for the Godot client (design piece T2).
//
// OFF by default. A listener is created only when MMO_DEBUG_CONTROL_PORT is set to a valid port; with
// the flag unset, MmoClientRoot never constructs this type and there is zero behavior change. The
// listener binds 127.0.0.1 exclusively (never 0.0.0.0), so no remote host can connect.
//
// Protocol: line-delimited JSON request/response over a raw TCP socket. One JSON object per line in,
// one JSON object per line out. The socket is polled once per frame from _Process, fully non-blocking,
// so it never adds frame hitches. Parsing/serialization (System.Text.Json) only runs when a full line
// has actually arrived; the common idle frame does a cheap Available check and returns.
//
// The channel never touches the filesystem or shell on behalf of a request. Its only disk write is the
// autopilot per-frame CSV under .run/, which the host owns.
internal sealed class DebugControlChannel : IDisposable
{
    // Bounded line buffer so a misbehaving/hostile-but-local client cannot make us allocate without limit.
    private const int MaxLineBytes = 8 * 1024;

    private readonly TcpListener _listener;
    private readonly IControlHost _host;
    private readonly List<Connection> _connections = new(2);
    private readonly List<Connection> _closedScratch = new(2);
    private bool _disposed;

    private DebugControlChannel(TcpListener listener, IControlHost host)
    {
        _listener = listener;
        _host = host;
    }

    // Returns null (no listener) when the flag is unset/invalid. Any bind failure is logged and also
    // yields null so a debug-port collision never takes down normal play.
    public static DebugControlChannel? TryCreate(IControlHost host)
    {
        var raw = System.Environment.GetEnvironmentVariable("MMO_DEBUG_CONTROL_PORT");
        if (string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return null;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            GD.Print($"Debug control channel listening on 127.0.0.1:{port} (MMO_DEBUG_CONTROL_PORT).");
            return new DebugControlChannel(listener, host);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Debug control channel failed to bind 127.0.0.1:{port}: {exception.Message}");
            return null;
        }
    }

    // Called once per frame from _Process. Non-blocking: accepts at most the currently pending
    // connections, then drains whole lines that have already arrived. No blocking reads, no sleeps.
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        while (_listener.Pending())
        {
            try
            {
                var socket = _listener.AcceptTcpClient();
                socket.NoDelay = true;
                _connections.Add(new Connection(socket));
            }
            catch (SocketException)
            {
                break;
            }
        }

        if (_connections.Count == 0)
        {
            return;
        }

        _closedScratch.Clear();
        foreach (var connection in _connections)
        {
            if (!PumpConnection(connection))
            {
                _closedScratch.Add(connection);
            }
        }

        foreach (var closed in _closedScratch)
        {
            closed.Dispose();
            _connections.Remove(closed);
        }
    }

    private bool PumpConnection(Connection connection)
    {
        try
        {
            var stream = connection.Stream;
            while (connection.Socket.Connected && connection.Socket.Available > 0)
            {
                var read = stream.Read(connection.ReadBuffer, 0, connection.ReadBuffer.Length);
                if (read <= 0)
                {
                    return false;
                }

                for (var i = 0; i < read; i++)
                {
                    var b = connection.ReadBuffer[i];
                    if (b == (byte)'\n')
                    {
                        var line = connection.TakeLine();
                        if (line.Length > 0)
                        {
                            var response = Dispatch(line);
                            WriteLine(stream, response);
                        }
                    }
                    else if (b != (byte)'\r')
                    {
                        if (!connection.AppendByte(b, MaxLineBytes))
                        {
                            // Overlong line: drop the connection rather than buffer unbounded.
                            return false;
                        }
                    }
                }
            }

            return connection.Socket.Connected;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void WriteLine(NetworkStream stream, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    // Parses one request line and produces one JSON response line. Errors are reported as
    // {"ok":false,"error":...} rather than throwing, so a bad request never disturbs the frame.
    private string Dispatch(string line)
    {
        string command;
        JsonElement root = default;
        var hasRoot = false;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
            hasRoot = true;
            command = root.TryGetProperty("cmd", out var cmdElement) && cmdElement.ValueKind == JsonValueKind.String
                ? cmdElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException exception)
        {
            return Error($"invalid json: {exception.Message}");
        }

        if (command.Length == 0)
        {
            return Error("missing 'cmd'");
        }

        try
        {
            return command switch
            {
                "move" => HandleMove(root, hasRoot),
                "stop" => HandleStop(),
                "chat" => HandleChat(root, hasRoot),
                "toggle_perf" => HandleTogglePerf(),
                "toggle_fullscreen" => HandleToggleFullscreen(),
                "autopilot" => HandleAutopilot(root, hasRoot),
                "telemetry" => HandleTelemetry(),
                "interp" => HandleInterp(),
                "entities" => HandleEntities(),
                "state" => HandleState(),
                "ping" => Ok(writer => writer.WriteString("pong", "ok")),
                _ => Error($"unknown cmd '{command}'")
            };
        }
        catch (Exception exception)
        {
            return Error($"command failed: {exception.Message}");
        }
    }

    // ---- Commands (input injection) ----------------------------------------------------------

    private string HandleMove(JsonElement root, bool hasRoot)
    {
        if (!hasRoot || !root.TryGetProperty("dir", out var dirElement) || dirElement.ValueKind != JsonValueKind.String)
        {
            return Error("move requires string 'dir' (N/NE/E/SE/S/SW/W/NW)");
        }

        if (!Enum.TryParse<Direction8>(dirElement.GetString(), ignoreCase: true, out var direction))
        {
            return Error($"unknown dir '{dirElement.GetString()}'");
        }

        var durationMs = ReadDouble(root, "durationMs", 0d);
        _host.BeginManualMove(direction, durationMs);
        return Ok(writer =>
        {
            writer.WriteString("dir", direction.ToString());
            writer.WriteNumber("durationMs", durationMs);
        });
    }

    private string HandleStop()
    {
        _host.StopMovement();
        return Ok(null);
    }

    private string HandleChat(JsonElement root, bool hasRoot)
    {
        if (!hasRoot || !root.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            return Error("chat requires string 'text'");
        }

        var text = textElement.GetString() ?? string.Empty;
        _host.SendChat(text);
        return Ok(writer => writer.WriteString("text", text));
    }

    private string HandleTogglePerf()
    {
        _host.TogglePerfHud();
        return Ok(null);
    }

    private string HandleToggleFullscreen()
    {
        _host.ToggleFullscreen();
        return Ok(null);
    }

    private string HandleAutopilot(JsonElement root, bool hasRoot)
    {
        var pattern = hasRoot && root.TryGetProperty("pattern", out var patternElement) && patternElement.ValueKind == JsonValueKind.String
            ? patternElement.GetString() ?? "square"
            : "square";
        var durationMs = ReadDouble(root, "durationMs", 30000d);
        if (!_host.TryBeginAutopilot(pattern, durationMs, out var error))
        {
            return Error(error);
        }

        return Ok(writer =>
        {
            writer.WriteString("pattern", pattern);
            writer.WriteNumber("durationMs", durationMs);
        });
    }

    // ---- Queries (telemetry readout) ---------------------------------------------------------

    private string HandleTelemetry()
    {
        var t = _host.ReadTelemetry();
        return Ok(writer =>
        {
            writer.WriteNumber("fps", t.Fps);
            writer.WriteNumber("frameMsLast", t.FrameMsLast);
            writer.WriteNumber("frameMsMax", t.FrameMsMax);
            writer.WriteStartObject("sectionMsLast");
            writer.WriteNumber("poll", t.PollMsLast);
            writer.WriteNumber("renderState", t.RenderStateMsLast);
            writer.WriteNumber("entities", t.EntitiesMsLast);
            writer.WriteNumber("camera", t.CameraMsLast);
            writer.WriteNumber("overlay", t.OverlayMsLast);
            writer.WriteEndObject();
            writer.WriteStartObject("sectionMsMax");
            writer.WriteNumber("poll", t.PollMsMax);
            writer.WriteNumber("renderState", t.RenderStateMsMax);
            writer.WriteNumber("entities", t.EntitiesMsMax);
            writer.WriteNumber("camera", t.CameraMsMax);
            writer.WriteNumber("overlay", t.OverlayMsMax);
            writer.WriteEndObject();
            writer.WriteStartObject("gc");
            writer.WriteNumber("gen0", t.Gc0);
            writer.WriteNumber("gen1", t.Gc1);
            writer.WriteNumber("gen2", t.Gc2);
            writer.WriteEndObject();
            writer.WriteNumber("hitchCount", t.HitchCount);
        });
    }

    private string HandleInterp()
    {
        var md = _host.ReadMovementDebug();
        return Ok(writer =>
        {
            writer.WriteNumber("queueDepth", md.QueueDepth);
            writer.WriteNumber("cadenceMs", md.EffectiveCadenceMs);
            writer.WriteString("confirmedTile", md.LastConfirmedTile?.ToString() ?? string.Empty);
            writer.WriteNumber("confirmedSnapshotSeq", md.LastConfirmedSnapshotSequence);
            writer.WriteNumber("latencyMs", md.LastLatencyMs);
        });
    }

    private string HandleEntities()
    {
        var states = _host.ReadEntities();
        return Ok(writer =>
        {
            writer.WriteStartArray("entities");
            foreach (var state in states)
            {
                writer.WriteStartObject();
                writer.WriteNumber("networkId", state.NetworkId);
                writer.WriteBoolean("isLocal", state.IsLocal);
                writer.WriteString("name", state.DisplayName);
                writer.WriteString("tile", state.AuthoritativeTile.ToString());
                writer.WriteStartObject("render");
                writer.WriteNumber("x", state.Position.X);
                writer.WriteNumber("y", state.Position.Y);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private string HandleState()
    {
        var s = _host.ReadState();
        return Ok(writer =>
        {
            writer.WriteString("connection", s.Connection);
            writer.WriteBoolean("loggedIn", s.LoggedIn);
            writer.WriteString("role", s.Role);
            writer.WriteString("zone", s.Zone);
            writer.WriteNumber("visibleEntities", s.VisibleEntities);
            writer.WriteString("localTile", s.LocalTile);
        });
    }

    // ---- Response helpers --------------------------------------------------------------------

    private static string Ok(Action<Utf8JsonWriter>? body)
    {
        using var buffer = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            body?.Invoke(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Error(string message)
    {
        using var buffer = new MemoryStream(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static double ReadDouble(JsonElement root, string name, double fallback)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value))
        {
            return value;
        }

        return fallback;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        _connections.Clear();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }
    }

    private sealed class Connection : IDisposable
    {
        private byte[] _line = new byte[256];
        private int _lineLength;

        public Connection(TcpClient socket)
        {
            Socket = socket;
            Stream = socket.GetStream();
        }

        public TcpClient Socket { get; }

        public NetworkStream Stream { get; }

        public byte[] ReadBuffer { get; } = new byte[2048];

        public bool AppendByte(byte b, int maxLineBytes)
        {
            if (_lineLength >= maxLineBytes)
            {
                return false;
            }

            if (_lineLength == _line.Length)
            {
                Array.Resize(ref _line, _line.Length * 2);
            }

            _line[_lineLength++] = b;
            return true;
        }

        public string TakeLine()
        {
            var text = Encoding.UTF8.GetString(_line, 0, _lineLength);
            _lineLength = 0;
            return text;
        }

        public void Dispose()
        {
            try
            {
                Stream.Dispose();
            }
            catch (Exception)
            {
                // Best effort on teardown.
            }

            Socket.Dispose();
        }
    }
}
