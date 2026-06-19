using Mmo.Client.Core;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// End-to-end S47b proof: the REAL delta encoding (EntityStateDelta + ProtocolCodec, the same policy the
// server uses) round-trips through the REAL client decode/apply (MmoClient), with the server-side acked
// baseline (ClientSession) and the client-side contiguity tracker (SnapshotContiguityTracker) modeling the
// UDP channel as deliver/drop. The bar: even when a MIDDLE snapshot is dropped while an entity steps
// several tiles, the client's reconstructed tile converges EXACTLY to the server's — a skipped cumulative
// step-delta would otherwise permanently desync the position.
//
// The encoder here mirrors GameServer.BuildEntityStateRow exactly (complete/no-baseline ⇒ absolute, else a
// delta against the acked baseline) but calls the shared EntityStateDelta policy, so it exercises the real
// wire encoding rather than a re-implementation.
public sealed class SnapshotDeltaConvergenceTests
{
    private const uint EntityNetworkId = 7;

    [Fact]
    public void SteadyStepsWithNoLossKeepClientTileExactlyOnServer()
    {
        var harness = new DeltaHarness(EntityNetworkId);

        // Baseline (complete) establishes the entity on the client.
        harness.SendComplete();
        Assert.Equal(harness.ServerTile, harness.ClientTile);

        // Step east several times; every snapshot delivered + acked. Each delta is a single-tile STEP.
        for (var i = 0; i < 6; i++)
        {
            harness.Step(Direction8.E);
            harness.SendDelta(deliver: true, ack: true);
            Assert.Equal(harness.ServerTile, harness.ClientTile);
        }

        // The deltas were actually steps, not absolutes (the encoding under test).
        Assert.True(harness.LastRowWasStep);
    }

    [Fact]
    public void DroppedMiddleSnapshotConvergesExactlyOnceTheGapIsFilledOrRebaselined()
    {
        var harness = new DeltaHarness(EntityNetworkId);
        harness.SendComplete();

        // Seq S1: step E, delivered + acked → baseline advances, client on server.
        harness.Step(Direction8.E);
        harness.SendDelta(deliver: true, ack: true);
        Assert.Equal(harness.ServerTile, harness.ClientTile);

        // Seq S2: step E, DROPPED (never acked). The server's baseline stays at the pre-S2 tile.
        harness.Step(Direction8.E);
        harness.SendDelta(deliver: false, ack: false);

        // Seq S3: step E again. Because the baseline is two tiles behind current (S2 unacked), the encoder
        // emits ABSOLUTE coords — so even though the client missed S2, applying S3 absolute lands it exactly
        // on the server tile. This is the self-healing the contiguous ack + absolute-on-non-unit-move buys.
        harness.Step(Direction8.E);
        harness.SendDelta(deliver: true, ack: true);
        Assert.False(harness.LastRowWasStep); // non-unit baseline → absolute, not a corrupting step
        Assert.Equal(harness.ServerTile, harness.ClientTile);

        // Continue stepping with full delivery; the client stays exactly on the server.
        for (var i = 0; i < 4; i++)
        {
            harness.Step(Direction8.E);
            harness.SendDelta(deliver: true, ack: true);
            Assert.Equal(harness.ServerTile, harness.ClientTile);
        }
    }

    [Fact]
    public void GapFilledByReorderConvergesAndResumesStepDeltas()
    {
        var harness = new DeltaHarness(EntityNetworkId);
        harness.SendComplete();

        harness.Step(Direction8.E);
        harness.SendDelta(deliver: true, ack: true);

        // S2 step delivered to the CLIENT but its ack is withheld (reorder: ack stalls at the gap).
        harness.Step(Direction8.E);
        harness.SendDelta(deliver: true, ack: false);
        Assert.Equal(harness.ServerTile, harness.ClientTile); // client applied it; only the ack stalled

        // Now the ack catches up (gap filled): baseline advances, and the next move is a clean unit step.
        harness.DeliverPendingAcks();
        harness.Step(Direction8.E);
        harness.SendDelta(deliver: true, ack: true);
        Assert.True(harness.LastRowWasStep);
        Assert.Equal(harness.ServerTile, harness.ClientTile);
    }

    [Fact]
    public void FacingAndDepletedChangesRideTheBitmaskWithoutMovingTheTile()
    {
        // A resource node: never moves, but depletes (and we exercise a facing-only delta too).
        var harness = new DeltaHarness(EntityNetworkId);
        harness.SendComplete();

        harness.SetFacing(Direction8.W);
        harness.SendDelta(deliver: true, ack: true);
        Assert.Equal(Direction8.W, harness.ClientFacing);
        Assert.Equal(harness.ServerTile, harness.ClientTile); // unchanged tile, position omitted

        harness.SetDepleted(true);
        harness.SendDelta(deliver: true, ack: true);
        Assert.True(harness.ClientDepleted);
        Assert.Equal(Direction8.W, harness.ClientFacing); // facing unchanged this row, preserved
    }

    // Couples the real server baseline, the real shared encoder, the real codec, and the real client decode
    // over an explicit deliver/drop channel. No sockets.
    private sealed class DeltaHarness
    {
        private readonly uint _networkId;
        private readonly ClientSession _session = new(null!);
        private readonly SnapshotContiguityTracker _tracker = new();
        private readonly MmoClient _client;

        private TileCoord _tile = new(100, 100);
        private Direction8 _facing = Direction8.S;
        private bool _depleted;
        private uint _revision = 1;
        private uint _serverTick = 1;

        // Acks the client owes but that have not yet been delivered to the server (reorder/withheld).
        private readonly List<uint> _withheldAcks = [];

        public DeltaHarness(uint networkId)
        {
            _networkId = networkId;
            _client = new MmoClient(
                new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
                new ClientMovementTrace(false, null));
            _client.OutboundSinkForTests = (_, _) => { };
        }

        public bool LastRowWasStep { get; private set; }

        public TileCoord ServerTile => _tile;

        public TileCoord ClientTile
        {
            get
            {
                Assert.True(_client.TryGetEntity(_networkId, out var entity));
                return entity.Tile;
            }
        }

        public Direction8 ClientFacing
        {
            get
            {
                Assert.True(_client.TryGetEntity(_networkId, out var entity));
                return entity.Facing;
            }
        }

        public bool ClientDepleted
        {
            get
            {
                Assert.True(_client.TryGetEntity(_networkId, out var entity));
                return entity.Depleted;
            }
        }

        public void Step(Direction8 direction)
        {
            var delta = direction.Delta();
            _tile = _tile.Offset(delta.X, delta.Y);
            _facing = direction;
            _revision++;
        }

        public void SetFacing(Direction8 facing)
        {
            _facing = facing;
            _revision++;
        }

        public void SetDepleted(bool depleted)
        {
            _depleted = depleted;
            _revision++;
        }

        // A complete (baseline) snapshot: absolute, always delivered + acked.
        public void SendComplete()
        {
            var seq = _session.NextSnapshotSequence();
            var pending = _session.BeginPendingSnapshot(seq, _serverTick);
            pending.Add(_networkId, _revision, _tile, _facing, _depleted);

            var row = EntityStateDelta.EncodeAbsolute(_networkId, _tile, _facing, _depleted);
            LastRowWasStep = false;
            DeliverSnapshot(seq, isComplete: true, row);
            Ack(seq, isComplete: true);
            _serverTick++;
        }

        public void SendDelta(bool deliver, bool ack)
        {
            var seq = _session.NextSnapshotSequence();
            var pending = _session.BeginPendingSnapshot(seq, _serverTick);
            pending.Add(_networkId, _revision, _tile, _facing, _depleted);

            // Mirror GameServer.BuildEntityStateRow: absolute when the client has no acked baseline, else a
            // delta against the acked baseline.
            EntityStateSnapshot row;
            if (_session.TryGetAckedBaseline(_networkId, out var baseline))
            {
                row = EntityStateDelta.EncodeDelta(
                    _networkId, _tile, _facing, _depleted, baseline.Tile, baseline.Facing, baseline.Depleted);
            }
            else
            {
                row = EntityStateDelta.EncodeAbsolute(_networkId, _tile, _facing, _depleted);
            }

            LastRowWasStep = row.HasStepPosition;

            if (deliver)
            {
                DeliverSnapshot(seq, isComplete: false, row);
                if (ack)
                {
                    Ack(seq, isComplete: false);
                }
                else
                {
                    _withheldAcks.Add(seq);
                }
            }

            _serverTick++;
        }

        // Delivers any acks that were withheld (the client had received the snapshots but the ack lagged).
        public void DeliverPendingAcks()
        {
            foreach (var seq in _withheldAcks)
            {
                // The client recomputes its contiguous cursor and the server processes that ack.
                var contiguous = _tracker.Observe(seq);
                _session.AcknowledgeSnapshot(contiguous, _serverTick);
            }

            _withheldAcks.Clear();
            _serverTick++;
        }

        private void DeliverSnapshot(uint seq, bool isComplete, EntityStateSnapshot row)
        {
            // Encode on the wire and decode back, exactly as over the network, then hand to the real client.
            var message = new WorldSnapshotMessage(_serverTick, seq, 1, isComplete, 0, 1, new[] { row });
            var decoded = (WorldSnapshotMessage)ProtocolCodec.Decode(ProtocolCodec.Encode(message));
            _client.HandleMessageForTests(decoded);
        }

        private void Ack(uint seq, bool isComplete)
        {
            var contiguous = _tracker.Observe(seq, isComplete);
            _session.AcknowledgeSnapshot(contiguous, _serverTick);
        }
    }
}
