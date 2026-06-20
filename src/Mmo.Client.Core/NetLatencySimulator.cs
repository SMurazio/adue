using LiteNetLib;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

// S93 — client-only artificial network-latency injector (debug tooling). Holds outbound and inbound traffic
// for a symmetric one-way delay so the movement models can be felt under real-world RTT without a remote
// server. The same one-way value is applied to BOTH directions, so the felt round-trip ≈ 2× LatencyMs.
//
// Why a custom queue and not LiteNetLib's built-in NetManager.SimulateLatency: those config fields are
// declared in the shipped LiteNetLib 2.1.4 assembly, but the delay-handling code (HandleSimulateLatency and
// the internal packet-holding queue) is `#if DEBUG`/SIMULATE_NETWORK-gated and was compiled OUT of the
// Release NuGet DLL — reflection confirms the HandleSimulateLatency method is ABSENT — so flipping the fields
// does nothing. See S93 review-request for the verification.
//
// Mechanism: each direction is an independent FIFO queue keyed on a monotonic releaseAt = enqueueTime +
// LatencyMs. Poll(now) drains every item whose releaseAt <= now in arrival order. LatencyMs == 0 means the
// simulator is INACTIVE and callers bypass it entirely (immediate send/handle), so the default path is
// unchanged with zero overhead.
internal sealed class NetLatencySimulator
{
    // Outbound: a message the client wants to send, plus its delivery method.
    private readonly Queue<DelayedOutbound> _outbound = new();
    // Inbound: a decoded message awaiting delivery to HandleMessage.
    private readonly Queue<DelayedInbound> _inbound = new();
    private int _latencyMs;

    // The active one-way delay in ms. 0 = injection disabled (the simulator is inactive; both queues are
    // empty and bypassed). Negative inputs are clamped to 0.
    public int LatencyMs => _latencyMs;

    // Whether injection is active (latency > 0). When false the caller MUST bypass the queues so the default
    // path is byte-for-byte unchanged.
    public bool Active => _latencyMs > 0;

    // Whether either queue still holds items. Used so Poll keeps flushing in-flight traffic even right after
    // latency is lowered to 0 (already-queued items must still drain on their original releaseAt rather than
    // being stranded). False when both queues are empty ⇒ Poll can skip the flush entirely.
    public bool HasPending => _outbound.Count > 0 || _inbound.Count > 0;

    // Live-sets the one-way delay. Lowering it (incl. to 0) does NOT retroactively re-time already-queued
    // items; they keep their original releaseAt and drain on the next due Poll. Setting 0 disables injection
    // for traffic enqueued afterwards (in-flight items still flush by their releaseAt). Clamped to >= 0.
    public void SetLatencyMs(int latencyMs)
    {
        _latencyMs = latencyMs < 0 ? 0 : latencyMs;
    }

    // Enqueue an outbound message for delayed sending. releaseAt = now + LatencyMs. Callers only reach here
    // while Active; FIFO order is preserved per the single outbound queue.
    public void EnqueueOutbound(IProtocolMessage message, DeliveryMethod deliveryMethod, TimeSpan now)
    {
        _outbound.Enqueue(new DelayedOutbound(message, deliveryMethod, now + TimeSpan.FromMilliseconds(_latencyMs)));
    }

    // Buffer an inbound (already-decoded) message for delayed handling. releaseAt = now + LatencyMs. Callers
    // only reach here while Active; arrival order is preserved per the single inbound queue.
    public void EnqueueInbound(IProtocolMessage message, TimeSpan now)
    {
        _inbound.Enqueue(new DelayedInbound(message, now + TimeSpan.FromMilliseconds(_latencyMs)));
    }

    // Drains every outbound item whose releaseAt <= now, in FIFO order, invoking `send` for each. Stops at the
    // first not-yet-due item (the queue is monotonic in releaseAt because LatencyMs only grows the deadline of
    // later enqueues; a lowered latency never re-times earlier items, so the head is always the soonest-due).
    public void FlushOutboundDue(TimeSpan now, Action<IProtocolMessage, DeliveryMethod> send)
    {
        while (_outbound.Count > 0 && _outbound.Peek().ReleaseAt <= now)
        {
            var item = _outbound.Dequeue();
            send(item.Message, item.DeliveryMethod);
        }
    }

    // Drains every inbound item whose releaseAt <= now, in arrival order, invoking `handle` for each.
    public void FlushInboundDue(TimeSpan now, Action<IProtocolMessage> handle)
    {
        while (_inbound.Count > 0 && _inbound.Peek().ReleaseAt <= now)
        {
            var item = _inbound.Dequeue();
            handle(item.Message);
        }
    }

    private readonly record struct DelayedOutbound(IProtocolMessage Message, DeliveryMethod DeliveryMethod, TimeSpan ReleaseAt);

    private readonly record struct DelayedInbound(IProtocolMessage Message, TimeSpan ReleaseAt);
}
