namespace Mmo.Client.Core;

// The latest server reply to an InteractRequest, surfaced for a HUD to render as feedback. Reason is the
// server's short machine-readable code on failure ("too_far", "depleted", "inventory_full",
// "rate_limited", "not_resource", "no_target", ...) and empty on success. Sequence is a monotonic
// per-result counter so a view can detect a fresh result (including two identical failures in a row)
// without subscribing to an event.
public readonly record struct InteractResultInfo(bool Success, string Reason, long Sequence);
