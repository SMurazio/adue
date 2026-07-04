namespace Mmo.Server.Data;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): the no-DB seam for IEcologyRepository — GameServer's default
// when no repository is supplied. Every existing test suite (RegionSpawnerIntegrationTests, AuthoredWorldTests,
// EcologyWireTests, ...) constructs GameServer with just an ICharacterRepository fake and never touches ecology
// persistence directly; this keeps all of them compiling and passing unchanged (LoadAllAsync returns nothing, so
// EcologyState stays at its K-seed, exactly like "no saved rows yet"). Also the current stand-in for the
// Postgres provider (see the review briefing — Postgres has no region_populations migration/repository in this
// task's scope, so Program.cs wires this in for DatabaseProvider.Postgres rather than leaving ecology unwired).
public sealed class NullEcologyRepository : IEcologyRepository
{
    public Task<IReadOnlyList<RegionPopulationRecord>> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RegionPopulationRecord>>([]);

    public Task SaveAllAsync(IReadOnlyList<RegionPopulationRecord> rows, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
