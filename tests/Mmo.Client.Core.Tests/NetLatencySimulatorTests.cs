using LiteNetLib;
using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S93 — unit tests for the client-only artificial-latency injector. Drives the simulator with explicit,
// deterministic TimeSpans (no wall clock) so release ordering/timing is exact. Covers both the outbound and
// inbound queues: an item enqueued with a latency is NOT released before enqueueTime + latency, IS released
// at/after it, arrival (FIFO) order is preserved, and latency == 0 means inactive (callers bypass entirely).
public sealed class NetLatencySimulatorTests
{
    private static readonly IProtocolMessage MsgA = new ChatSendMessage("a");
    private static readonly IProtocolMessage MsgB = new ChatSendMessage("b");
    private static readonly IProtocolMessage MsgC = new ChatSendMessage("c");

    [Fact]
    public void ZeroLatencyIsInactiveSoCallersBypassTheQueues()
    {
        var sim = new NetLatencySimulator();

        Assert.Equal(0, sim.LatencyMs);
        Assert.False(sim.Active);
        Assert.False(sim.HasPending);
    }

    [Fact]
    public void NegativeLatencyClampsToZeroAndStaysInactive()
    {
        var sim = new NetLatencySimulator();

        sim.SetLatencyMs(-50);

        Assert.Equal(0, sim.LatencyMs);
        Assert.False(sim.Active);
    }

    [Fact]
    public void OutboundIsNotReleasedBeforeTheDeadlineAndIsReleasedAtOrAfterIt()
    {
        var sim = new NetLatencySimulator();
        sim.SetLatencyMs(100);
        var sent = new List<IProtocolMessage>();

        sim.EnqueueOutbound(MsgA, DeliveryMethod.ReliableOrdered, TimeSpan.FromMilliseconds(0));
        Assert.True(sim.HasPending);

        // Before the deadline (0 + 100 = 100ms): nothing released.
        sim.FlushOutboundDue(TimeSpan.FromMilliseconds(99), (m, _) => sent.Add(m));
        Assert.Empty(sent);
        Assert.True(sim.HasPending);

        // Exactly at the deadline: released.
        sim.FlushOutboundDue(TimeSpan.FromMilliseconds(100), (m, _) => sent.Add(m));
        Assert.Equal(new[] { MsgA }, sent);
        Assert.False(sim.HasPending);
    }

    [Fact]
    public void OutboundPreservesFifoOrderAndDeliveryMethod()
    {
        var sim = new NetLatencySimulator();
        sim.SetLatencyMs(50);
        var sent = new List<(IProtocolMessage Msg, DeliveryMethod Method)>();

        sim.EnqueueOutbound(MsgA, DeliveryMethod.ReliableOrdered, TimeSpan.FromMilliseconds(0));
        sim.EnqueueOutbound(MsgB, DeliveryMethod.Sequenced, TimeSpan.FromMilliseconds(10));
        sim.EnqueueOutbound(MsgC, DeliveryMethod.Unreliable, TimeSpan.FromMilliseconds(20));

        // At 55ms only A (deadline 50) and B (deadline 60? no) — A due at 50, B due at 60, C at 70.
        sim.FlushOutboundDue(TimeSpan.FromMilliseconds(55), (m, d) => sent.Add((m, d)));
        Assert.Equal(new[] { MsgA }, sent.Select(s => s.Msg));
        Assert.Equal(DeliveryMethod.ReliableOrdered, sent[0].Method);

        // At 70ms the remaining two drain in arrival order with their own delivery methods.
        sim.FlushOutboundDue(TimeSpan.FromMilliseconds(70), (m, d) => sent.Add((m, d)));
        Assert.Equal(new[] { MsgA, MsgB, MsgC }, sent.Select(s => s.Msg));
        Assert.Equal(DeliveryMethod.Sequenced, sent[1].Method);
        Assert.Equal(DeliveryMethod.Unreliable, sent[2].Method);
        Assert.False(sim.HasPending);
    }

    [Fact]
    public void InboundIsNotReleasedBeforeTheDeadlineAndPreservesArrivalOrder()
    {
        var sim = new NetLatencySimulator();
        sim.SetLatencyMs(100);
        var handled = new List<IProtocolMessage>();

        sim.EnqueueInbound(MsgA, TimeSpan.FromMilliseconds(0));   // due 100
        sim.EnqueueInbound(MsgB, TimeSpan.FromMilliseconds(30));  // due 130

        // Before either deadline.
        sim.FlushInboundDue(TimeSpan.FromMilliseconds(99), handled.Add);
        Assert.Empty(handled);

        // First deadline elapsed but not the second: only A drains.
        sim.FlushInboundDue(TimeSpan.FromMilliseconds(100), handled.Add);
        Assert.Equal(new[] { MsgA }, handled);
        Assert.True(sim.HasPending);

        // Second deadline elapsed: B drains, in arrival order.
        sim.FlushInboundDue(TimeSpan.FromMilliseconds(130), handled.Add);
        Assert.Equal(new[] { MsgA, MsgB }, handled);
        Assert.False(sim.HasPending);
    }

    [Fact]
    public void RaisingLatencyKeepsHeadSoonestDueSoFlushStopsAtFirstNotDueItem()
    {
        var sim = new NetLatencySimulator();
        var handled = new List<IProtocolMessage>();

        sim.SetLatencyMs(50);
        sim.EnqueueInbound(MsgA, TimeSpan.FromMilliseconds(0)); // due 50
        sim.SetLatencyMs(200);
        sim.EnqueueInbound(MsgB, TimeSpan.FromMilliseconds(0)); // due 200

        // At 60ms: A is due, B is not. Flush must release A and stop (the head is the soonest-due).
        sim.FlushInboundDue(TimeSpan.FromMilliseconds(60), handled.Add);
        Assert.Equal(new[] { MsgA }, handled);
        Assert.True(sim.HasPending);

        sim.FlushInboundDue(TimeSpan.FromMilliseconds(200), handled.Add);
        Assert.Equal(new[] { MsgA, MsgB }, handled);
    }

    [Fact]
    public void LoweringLatencyToZeroStillDrainsAlreadyQueuedItemsOnOriginalDeadline()
    {
        var sim = new NetLatencySimulator();
        var handled = new List<IProtocolMessage>();

        sim.SetLatencyMs(100);
        sim.EnqueueInbound(MsgA, TimeSpan.FromMilliseconds(0)); // due 100

        // Latency goes back to 0: the simulator is now inactive for NEW traffic, but the in-flight item must
        // still drain on its original deadline (HasPending stays true until it does).
        sim.SetLatencyMs(0);
        Assert.False(sim.Active);
        Assert.True(sim.HasPending);

        sim.FlushInboundDue(TimeSpan.FromMilliseconds(99), handled.Add);
        Assert.Empty(handled);

        sim.FlushInboundDue(TimeSpan.FromMilliseconds(100), handled.Add);
        Assert.Equal(new[] { MsgA }, handled);
        Assert.False(sim.HasPending);
    }

    [Fact]
    public void ClientApiFlipsTheSimulatedLatencyValueAndDefaultsToOff()
    {
        using var client = new MmoClient(
            new ClientConnectionOptions("127.0.0.1", 1, "test", "account", "display"),
            new ClientMovementTrace(false, null));

        // Default: off (0 = unchanged default path).
        Assert.Equal(0, client.SimulatedLatencyMs);

        client.SetSimulatedLatencyMs(100);
        Assert.Equal(100, client.SimulatedLatencyMs);

        // Negative clamps to 0 (off).
        client.SetSimulatedLatencyMs(-5);
        Assert.Equal(0, client.SimulatedLatencyMs);
    }
}
