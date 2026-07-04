using System.Text.Json;

namespace Mmo.Server.Runtime;

// ECOLOGY E1 (docs/ecology-v1-design.md §3/§8): the table of authored ecology REGIONS (rectangles on the one
// sandbox zone) + their per-monster-type {K, rPerMinute, maxLive} content, loaded from Content/ecology.json.
// Mirrors MonsterTypeRegistry's load/clamp/code-seed-fallback pattern EXACTLY: a clamped load from a loose data
// manifest is the authoritative runtime source; a code-seeded registry (the §7 starter regions, byte-for-byte)
// is the safety net when the file is missing or fails to parse. This registry is pure CONTENT (region rects +
// per-type K/r/maxLive) — it owns no mutable stock/pressure; EcologyState owns that (constructed FROM this).
public sealed class EcologyRegistry
{
    // Clamps mirroring the §7 authoring note — EXCEPT MinK, raised 1 -> 3 by the E1 independent review: with
    // K <= 2 the absolute stock floor (0.5) sits AT/ABOVE the 0.25K depleted band, making the DEPLETED state
    // unreachable and voiding D2's suppression mechanic for that region. K >= 3 keeps floor (0.5) < band (0.75).
    private const double MinK = 3d;
    private const double MaxK = 64d;
    private const double MinRPerMinute = 0.05d;
    private const double MaxRPerMinute = 10d;
    private const int MinMaxLive = 1;
    private const int MaxMaxLive = 32;

    private readonly Dictionary<string, EcologyRegion> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EcologyRegion> _ordered = [];

    // The code-seeded ctor — the FALLBACK + test-seed, used when Content/ecology.json is absent or fails to
    // parse. Seeds the §7 starter regions byte-for-byte: Slime Hollow (slimes, K=10, r=1.0/min), Eastern
    // Scrubland (gnolls, K=8, r=0.4/min), The Verge (both types, K=6 each, r=0.25/min). Rects are the REAL wing
    // bounds from AuthoredMaps.BuildTownAndFloor1 (the "WEST WING Slime Hollow"/"EAST WING Gnoll Scrubland"/"THE
    // VERGE" stamp-program comments) — see the review briefing for the exact rationale. maxLive defaults to the
    // per-type K rounded up (a region's live cap tracks its carrying capacity at HEALTHY; D7's OVERGROWN +50% is
    // an E2 runtime modifier, not authored content).
    public EcologyRegistry()
        : this(seed: true)
    {
    }

    private EcologyRegistry(bool seed)
    {
        if (!seed)
        {
            return;
        }

        Add(new EcologyRegion("slime_hollow", "Slime Hollow", 20, 120, 140, 220, new Dictionary<string, EcologyTypeConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["slime"] = new EcologyTypeConfig(10d, 1.0d, 10),
        }));
        Add(new EcologyRegion("eastern_scrubland", "Eastern Scrubland", 250, 120, 364, 220, new Dictionary<string, EcologyTypeConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["gnoll"] = new EcologyTypeConfig(8d, 0.4d, 8),
        }));
        Add(new EcologyRegion("the_verge", "The Verge", 100, 300, 300, 370, new Dictionary<string, EcologyTypeConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["slime"] = new EcologyTypeConfig(6d, 0.25d, 6),
            ["gnoll"] = new EcologyTypeConfig(6d, 0.25d, 6),
        }));
    }

    // Loads the registry from the Content/ecology.json DATA MANIFEST shape (mirrors MonsterTypeRegistry.
    // FromManifestJson). Validation: non-empty id/displayName per region, a valid rect (min <= max on both axes),
    // at least one type per region, no duplicate region ids, no duplicate type ids WITHIN a region. Every provided
    // K/rPerMinute/maxLive is CLAMPED to the bounds above (the data file cannot author an out-of-range region); an
    // empty/malformed manifest throws a clear ArgumentException so the caller's loud fallback kicks in.
    public static EcologyRegistry FromManifestJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Ecology manifest is empty.", nameof(json));
        }

        EcologyManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<EcologyManifestDto>(json, ManifestJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Ecology manifest is not valid JSON: {ex.Message}", nameof(json), ex);
        }

        if (manifest?.Regions is null || manifest.Regions.Count == 0)
        {
            throw new ArgumentException("Ecology manifest has no regions.", nameof(json));
        }

        var registry = new EcologyRegistry(seed: false);
        foreach (var dto in manifest.Regions)
        {
            if (dto is null)
            {
                throw new ArgumentException("Ecology manifest contains a null region entry.", nameof(json));
            }

            if (string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new ArgumentException("Ecology manifest region is missing a non-empty 'id'.", nameof(json));
            }

            if (string.IsNullOrWhiteSpace(dto.DisplayName))
            {
                throw new ArgumentException(
                    $"Ecology region '{dto.Id}' is missing a non-empty 'displayName'.", nameof(json));
            }

            if (registry._regions.ContainsKey(dto.Id))
            {
                throw new ArgumentException(
                    $"Ecology manifest has a duplicate region id '{dto.Id}'.", nameof(json));
            }

            if (dto.MinX is null || dto.MinY is null || dto.MaxX is null || dto.MaxY is null)
            {
                throw new ArgumentException(
                    $"Ecology region '{dto.Id}' is missing a rect bound (minX/minY/maxX/maxY).", nameof(json));
            }

            if (dto.MinX > dto.MaxX || dto.MinY > dto.MaxY)
            {
                throw new ArgumentException(
                    $"Ecology region '{dto.Id}' has an invalid rect (min must be <= max on both axes).", nameof(json));
            }

            if (dto.Types is null || dto.Types.Count == 0)
            {
                throw new ArgumentException($"Ecology region '{dto.Id}' has no types.", nameof(json));
            }

            var types = new Dictionary<string, EcologyTypeConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var typeDto in dto.Types)
            {
                if (typeDto is null)
                {
                    throw new ArgumentException($"Ecology region '{dto.Id}' has a null type entry.", nameof(json));
                }

                if (string.IsNullOrWhiteSpace(typeDto.TypeId))
                {
                    throw new ArgumentException(
                        $"Ecology region '{dto.Id}' has a type entry missing a non-empty 'typeId'.", nameof(json));
                }

                if (types.ContainsKey(typeDto.TypeId))
                {
                    throw new ArgumentException(
                        $"Ecology region '{dto.Id}' has a duplicate type id '{typeDto.TypeId}'.", nameof(json));
                }

                var k = Math.Clamp(typeDto.K ?? MinK, MinK, MaxK);
                var r = Math.Clamp(typeDto.RPerMinute ?? MinRPerMinute, MinRPerMinute, MaxRPerMinute);
                var maxLive = Math.Clamp(typeDto.MaxLive ?? MinMaxLive, MinMaxLive, MaxMaxLive);
                types[typeDto.TypeId] = new EcologyTypeConfig(k, r, maxLive);
            }

            registry.Add(new EcologyRegion(dto.Id, dto.DisplayName, dto.MinX.Value, dto.MinY.Value, dto.MaxX.Value, dto.MaxY.Value, types));
        }

        return registry;
    }

    // Tolerant of camelCase casing, `//` comments, and trailing commas — but STRICT on unknown members (mirrors
    // MonsterTypeRegistry.ManifestJsonOptions): a typo'd field fails loudly instead of silently defaulting.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private void Add(EcologyRegion region)
    {
        _regions[region.Id] = region;
        _ordered.Add(region);
    }

    // All regions in registration/manifest order — the admin dump + EcologyState's seed pass iterate this.
    public IReadOnlyList<EcologyRegion> Regions => _ordered;

    // Resolves a region by id (case-insensitive). False for an unknown id.
    public bool TryGet(string id, out EcologyRegion region) => _regions.TryGetValue(id, out region!);

    // Which authored region (if any) contains tile (x, y) — for E2's kill hook ("did this dead monster's tile
    // belong to a region?") and RUMOR/admin lookups. Rects are authored non-overlapping (§7); if a future manifest
    // authors overlapping rects, the FIRST match in manifest order wins (documented, not defended against here —
    // overlap is a content-authoring bug, not a runtime concern this registry needs to arbitrate).
    public bool TryGetRegionAt(int tileX, int tileY, out EcologyRegion region)
    {
        foreach (var candidate in _ordered)
        {
            if (tileX >= candidate.MinX && tileX <= candidate.MaxX && tileY >= candidate.MinY && tileY <= candidate.MaxY)
            {
                region = candidate;
                return true;
            }
        }

        region = null!;
        return false;
    }

    // The on-disk manifest shape. Property names are camelCase (matched case-insensitively); every region requires
    // its rect bounds + at least one type entry (no optional/omittable region-level fields — unlike MonsterType,
    // there is no sensible "default region", so nothing here falls back to a code default on a per-field basis).
    private sealed record EcologyManifestDto(List<EcologyRegionDto?>? Regions);

    private sealed record EcologyRegionDto(
        string? Id,
        string? DisplayName,
        int? MinX,
        int? MinY,
        int? MaxX,
        int? MaxY,
        List<EcologyTypeDto?>? Types);

    private sealed record EcologyTypeDto(string? TypeId, double? K, double? RPerMinute, int? MaxLive);
}

// One authored region: a display name + a tile rect + its per-monster-type content (K/r/maxLive). Immutable —
// EcologyState owns the mutable per-region×type {stock, pressure} it seeds FROM this.
public sealed class EcologyRegion
{
    public EcologyRegion(
        string id, string displayName, int minX, int minY, int maxX, int maxY, IReadOnlyDictionary<string, EcologyTypeConfig> types)
    {
        Id = id;
        DisplayName = displayName;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        Types = types;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public int MinX { get; }
    public int MinY { get; }
    public int MaxX { get; }
    public int MaxY { get; }

    // typeId (case-insensitive) -> its authored {K, rPerMinute, maxLive} in this region.
    public IReadOnlyDictionary<string, EcologyTypeConfig> Types { get; }
}

// One region×type's AUTHORED content — carrying capacity, growth rate, and live cap. Immutable content (NOT the
// mutable live stock/pressure EcologyState tracks). K/RPerMinute/MaxLive are already clamped by the loader.
public readonly record struct EcologyTypeConfig(double K, double RPerMinute, int MaxLive);
