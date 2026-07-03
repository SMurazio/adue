namespace Mmo.Server.Configuration;

public enum SpawnDistribution
{
    Distributed,
    Clustered,
    Scattered,

    // AUTHORED-MAP M3 (D4): spawn on the authored map's `S` plaza anchors (round-robin). The
    // FromEnvironment default — "cozy base" starts with waking up in town. On a map with no authored
    // anchors (genVersion 1) Zone falls back to the historical Distributed grid, so a procedural dev
    // world booted without MMO_SPAWN_DISTRIBUTION behaves exactly as before. An explicit
    // distributed/clustered/scattered env value still overrides authored anchors (stress/dev).
    Authored
}
