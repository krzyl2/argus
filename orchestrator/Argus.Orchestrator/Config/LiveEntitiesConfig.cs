namespace Argus.Orchestrator.Config;

/// <summary>
/// Thread-safe live configuration holder for <see cref="EntitiesConfig"/>.
/// Allows atomic swap of the configuration reference and notifies subscribers via
/// <see cref="ConfigChanged"/> after the exchange (CFG-04 reload mechanism).
/// </summary>
public interface ILiveEntitiesConfig
{
    /// <summary>Returns the current <see cref="EntitiesConfig"/> reference. Never null.</summary>
    EntitiesConfig Get();

    /// <summary>
    /// Atomically replaces the current config reference with <paramref name="newConfig"/>,
    /// then fires <see cref="ConfigChanged"/>. Subscribers that call <see cref="Get"/> inside
    /// the event handler observe the new reference.
    /// </summary>
    void Swap(EntitiesConfig newConfig);

    /// <summary>
    /// Raised after each successful <see cref="Swap"/>. Handlers may call <see cref="Get"/>
    /// and will see the newly swapped reference (event fires strictly after the exchange).
    /// </summary>
    event EventHandler? ConfigChanged;
}

/// <summary>
/// Production implementation of <see cref="ILiveEntitiesConfig"/> using a volatile reference
/// and <see cref="System.Threading.Interlocked.Exchange"/> for atomic swap semantics.
///
/// Single writer (save endpoint), many readers (worker threads + Kestrel) — no lock contention.
/// Mirrors the HaSensorRegistry volatile immutable-reference pattern (Phase 2 analog).
/// </summary>
public sealed class LiveEntitiesConfig : ILiveEntitiesConfig
{
    private volatile EntitiesConfig _current;

    /// <summary>Initialises the holder with the given <paramref name="initial"/> config.</summary>
    public LiveEntitiesConfig(EntitiesConfig initial)
        => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <inheritdoc/>
    public EntitiesConfig Get() => _current;

    /// <inheritdoc/>
    public void Swap(EntitiesConfig newConfig)
    {
        Interlocked.Exchange(ref _current, newConfig);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public event EventHandler? ConfigChanged;
}
