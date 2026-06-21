using System;
using System.Collections.Generic;
using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S106 — a live MovementSpeedChanged (the F6 "Move speed" dropdown -> /speed -> server -> MovementSpeedChanged)
// must retune the local predictor's cadence so prediction tracks the new speed with no desync. Scoped to
// UoClientDriven (the only supported movement mode; the other render modes are iced). The predictor attaches on
// the first move intent (SendMoveIntent -> EnsurePredictor), so the test sends one to establish it before
// asserting the cadence re-sync.
public sealed class MovementSpeedChangedCadenceTests
{
    private const uint LocalNetworkId = 1;
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140; // => 150 ms cadence (3 ticks).

    // 70 ms advertised cooldown quantises to 100 ms (2 ticks); 50 ms quantises to 50 ms (1 tick).
    private const ushort FasterCooldownMs = 70;
    private const double FasterCadenceMs = 100d;

    [Fact]
    public void SpeedChangeRetunesPredictorCadence()
    {
        var client = CreateLoggedInClientWithLocalEntity(MovementRenderMode.UoClientDriven, out _);
        client.SendMoveIntent(false, Direction8.S); // attaches the predictor (EnsurePredictor) at the base cadence.

        Assert.Equal(150d, client.LocalPredictorCadenceMsForTests);

        client.HandleMessageForTests(new MovementSpeedChangedMessage(LocalNetworkId, FasterCooldownMs));

        Assert.Equal(FasterCadenceMs, client.LocalPredictorCadenceMsForTests);
    }

    private static MmoClient CreateLoggedInClientWithLocalEntity(MovementRenderMode mode, out List<IProtocolMessage> outbound)
    {
        outbound = [];
        var captured = outbound;
        var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));
        client.OutboundSinkForTests = (message, _) => captured.Add(message);

        var characterId = Guid.NewGuid();
        var spawn = new TileCoord(10, 10);
        client.HandleMessageForTests(new ServerHelloMessage("test", ProtocolCodec.Version, TickRate, BaseStepCooldownMs, 30));
        var zone = new ZoneModel("zone", 64, 64, 0, 1);
        client.HandleMessageForTests(new ZoneInfoMessage("zone", 64, 64, 0, 1, zone.ContentHash));
        client.HandleMessageForTests(new LoginResultMessage(true, characterId, "Local", ClientRole.Player, spawn, ""));
        client.HandleMessageForTests(new EntitySpawnMessage(
            LocalNetworkId, characterId, EntityKind.Player, "Local", spawn, Direction8.S, StepCooldownMs: BaseStepCooldownMs));

        client.RenderMode = mode;
        return client;
    }
}
