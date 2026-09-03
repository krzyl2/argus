using System.Collections.Concurrent;

namespace Argus.Orchestrator.Detection;

/// <summary>
/// Last known warm-up status for a single-sensor entity, cached in memory (QUICK-warmup-status).
/// </summary>
public sealed record EntityStatusEntry(
    string EntityId,
    bool WarmedUp,
    int ReadingCount,
    int WarmUpWindow,
    // WS2: alert-layer calibration progress and state, surfaced by GET /api/sensors so a
    // "calibrating" or "storm" entity is visible rather than silently not alarming (A14).
    // Optional so the existing construction sites keep compiling unchanged.
    bool Calibrated = false,
    int CalibrationCount = 0,
    int CalibrationTarget = 0,
    string AlertState = "",
    // WS3 (D-E, F6-2): the calibrated band in the SENSOR'S OWN units, taken from
    // Verdict.expected/lower/upper (already on the wire — proto unchanged). This is what makes
    // one dimensionless threshold legible: the same high_threshold 0.5 renders as
    // "Norma: 107 W · alarm poza 92–122 W" on one sensor and a completely different band on
    // the next. Null before the first verdict — the UI must show "calibrating", never a
    // fabricated band.
    double? CalibratedExpected = null,
    double? CalibratedLower = null,
    double? CalibratedUpper = null,
    // Measured wall-clock spacing between readings, so the UI can turn a window in SAMPLES
    // into a span in hours (§7 #14: 720 samples is ~3 h on one sensor and ~78 h on another).
    double? MedianIntervalSec = null);

/// <summary>
/// In-memory last-status cache backing GET /api/sensors's per-entity warm-up projection
/// (QUICK-warmup-status). Single writer (ScoreStreamPipeline's write loop, per reading),
/// many readers (Kestrel) — mirrors the Batch/GroupStatusCache precedent, generalized to
/// a ConcurrentDictionary since the key set (entity_id) is open, not fixed.
/// </summary>
public interface IEntityStatusCache
{
    /// <summary>Returns the last cached entry for <paramref name="entityId"/>, or null if never scored.</summary>
    EntityStatusEntry? Get(string entityId);

    /// <summary>Stores/replaces the cached entry for the entry's EntityId.</summary>
    void Set(EntityStatusEntry entry);
}

/// <inheritdoc cref="IEntityStatusCache"/>
public sealed class EntityStatusCache : IEntityStatusCache
{
    private readonly ConcurrentDictionary<string, EntityStatusEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public EntityStatusEntry? Get(string entityId) =>
        _entries.TryGetValue(entityId, out var e) ? e : null;

    /// <inheritdoc/>
    public void Set(EntityStatusEntry entry) => _entries[entry.EntityId] = entry;
}
