namespace Mmo.Server.Data;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): one persisted region x type row — mirrors the
// region_populations table (migration 006) column-for-column. UpdatedAtTick is the server tick the row was
// written at (informational only today; no reader currently branches on it, but it is the natural "how stale is
// this" field for a future admin dump or migration-v2 diagnostic, so it is persisted from day one).
public sealed record RegionPopulationRecord(
    string RegionId,
    string TypeId,
    double Stock,
    double Pressure,
    long UpdatedAtTick);
