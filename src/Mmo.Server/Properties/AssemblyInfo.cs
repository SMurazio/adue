using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mmo.Server.Tests")]
// Client-core integration/parity tests (MmoClientIntegrationTests, LocalPlayerPredictorTests,
// TerrainParityTests, SnapshotGapConvergenceTests) drive the REAL server types (GameServer / WorldEntity /
// TerrainGenerator) to assert client behaviour against production rather than a mock.
[assembly: InternalsVisibleTo("Mmo.Client.Core.Tests")]
