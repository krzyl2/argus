using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.Orchestrator.Workers;

/// <summary>
/// BackgroundService that watches the directory containing entities.yaml for atomic renames
/// (produced by ConfigWriter's temp-then-rename pattern or external editors) and reloads
/// the live configuration via ILiveEntitiesConfig.Swap after a 300 ms debounce quiet period.
///
/// Design decisions:
/// - Subscribes ONLY to watcher.Renamed (not Changed) to avoid double-fire on File.Move
///   with overwrite (Pitfall 2 from 04-RESEARCH.md).
/// - 300 ms timer-reset debounce: N rapid Renamed events within 300 ms coalesce to exactly
///   one Load+Swap (Pitfall Patter 10 / SC4 "one reload per atomic write").
/// - Reload wraps Load+Swap in try/catch — invalid external edits are logged and ignored;
///   current live config is retained; pipeline never crashes (T-04-09 mitigation).
/// - Timer disposed on stoppingToken to prevent post-shutdown reloads (T-04-12 mitigation).
/// - Watcher never writes to disk — no infinite loop possible.
///
/// Testability seams (internal):
/// - InternalReload(string path) — direct call to Reload; bypasses FSW timing for unit tests.
/// - SimulateRenamedEvent(string fullPath) — triggers the same debounce logic as the real
///   Renamed handler; validates coalescing under a configurable interval.
/// - SetDebounceIntervalMs(int ms) — overrides the 300 ms interval for fast test runs.
/// </summary>
public sealed class ConfigFileWatcherService : BackgroundService
{
    private readonly ILiveEntitiesConfig _liveCfg;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<ConfigFileWatcherService> _logger;

    private volatile Timer? _debounce;
    private volatile int _debounceMs = 300;

    public ConfigFileWatcherService(
        ILiveEntitiesConfig liveCfg,
        ConnectionSettings settings,
        ILogger<ConfigFileWatcherService> logger)
    {
        _liveCfg  = liveCfg  ?? throw new ArgumentNullException(nameof(liveCfg));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entitiesPath = _settings.EntitiesPath ?? "/data/entities.yaml";
        var fullPath     = Path.GetFullPath(entitiesPath);
        var dir          = Path.GetDirectoryName(fullPath)!;
        var fileName     = Path.GetFileName(fullPath);

        using var watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter        = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        watcher.Renamed += (_, e) =>
        {
            if (!string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase)) return;
            ResetDebounce(fullPath);
        };

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { /* expected on cooperative shutdown */ }

        // Dispose the debounce timer so no reload fires against a torn-down container (T-04-12).
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
    }

    /// <summary>
    /// Resets the debounce timer atomically.
    /// Dispose old timer first (Interlocked.Exchange ensures only one winner per slot),
    /// then create a new one that fires Reload after the quiet period.
    /// </summary>
    private void ResetDebounce(string path)
    {
        Interlocked.Exchange(
            ref _debounce,
            new Timer(_ => Reload(path), null,
                TimeSpan.FromMilliseconds(_debounceMs),
                Timeout.InfiniteTimeSpan))
            ?.Dispose();
    }

    /// <summary>
    /// Load + Validate + Swap from disk. Invalid external edits are caught, logged,
    /// and ignored so the current live config is retained and the pipeline never crashes.
    /// Called by the debounce timer callback on a thread-pool thread.
    /// </summary>
    private void Reload(string path)
    {
        try
        {
            var newConfig = EntitiesConfigLoader.Load(path, _logger);
            _liveCfg.Swap(newConfig);
            _logger.LogInformation(LogEvents.ConfigReloadTriggered,
                "External edit detected — reloaded {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.ConfigFileWatcherReloadFailed, ex,
                "External edit to {Path} failed validation — keeping current config", path);
        }
    }

    // ─── Internal testability seams ────────────────────────────────────────────

    /// <summary>
    /// Directly invokes Reload — bypasses FileSystemWatcher timing for unit tests.
    /// </summary>
    internal void InternalReload(string path) => Reload(path);

    /// <summary>
    /// Triggers the same debounce logic as the real Renamed handler. The provided
    /// <paramref name="fullPath"/> is matched against EntitiesPath (OrdinalIgnoreCase)
    /// so the wrong-filename guard is exercised in tests.
    /// </summary>
    internal void SimulateRenamedEvent(string fullPath)
    {
        var entitiesPath = Path.GetFullPath(_settings.EntitiesPath ?? "/data/entities.yaml");
        var watchedName  = Path.GetFileName(entitiesPath);
        var eventName    = Path.GetFileName(fullPath);

        if (!string.Equals(eventName, watchedName, StringComparison.OrdinalIgnoreCase)) return;
        ResetDebounce(entitiesPath);
    }

    /// <summary>
    /// Overrides the 300 ms debounce interval for fast unit test runs.
    /// Must be called before SimulateRenamedEvent.
    /// </summary>
    internal void SetDebounceIntervalMs(int ms) => _debounceMs = ms;
}
