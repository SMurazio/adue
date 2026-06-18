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
    private double _elapsedSeconds;
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
        UpdateEntities(now);
        UpdateCamera();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.T })
        {
            _client?.SendChat($"hello from {PlayerName}");
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

    private void SendHeldMovement(TimeSpan now)
    {
        if (_client is null || !_client.IsLoggedIn)
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

    private static Direction8? CurrentDirection()
    {
        var x = (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0);
        var y = (Input.IsKeyPressed(Key.S) ? 1 : 0) - (Input.IsKeyPressed(Key.W) ? 1 : 0);
        return (x, y) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => null
        };
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
