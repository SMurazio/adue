namespace Mmo.Server.Data;

// ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): the persistence seam for region_populations. Sibling to
// ICharacterRepository, not an extension of it — EcologyState is a single boot-time load + one batch save per
// checkpoint (no per-character keying), so it gets its own small load-all/save-all surface rather than growing
// ICharacterRepository (which several test suites implement with private in-file fakes; adding members there
// would force every one of those fakes to grow ecology stubs it doesn't care about).
public interface IEcologyRepository
{
    // Every persisted row, unordered. Called once at boot (GameServer's ctor), after EcologyState has already
    // seeded every region x type at K — a missing row for a region x type simply leaves that K-seed in place.
    Task<IReadOnlyList<RegionPopulationRecord>> LoadAllAsync(CancellationToken cancellationToken);

    // Replaces every row's stock/pressure/updated_at_tick in ONE transaction (upsert-all, keyed on region_id +
    // type_id) — called on the existing persistence checkpoint cadence and once more on graceful shutdown. An
    // empty list is a no-op (mirrors ICharacterRepository.SaveItemsAsync's empty-changes short-circuit).
    Task SaveAllAsync(IReadOnlyList<RegionPopulationRecord> rows, CancellationToken cancellationToken);
}
