using Argus.Orchestrator.Config;
using Argus.Orchestrator.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for ConfigFileWatcherService — debounce coalescing, invalid-edit tolerance, valid reload.
///
/// Testability seam: the internal Reload(string path) method is called directly to assert
/// Load+Swap behaviour without inotify/FileSystemWatcher timing.
/// Debounce coalescing is tested via SimulateRenamedEvent with a short interval so the timer
/// fires quickly in the test environment.
/// </summary>
public class ConfigFileWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigFileWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"argus-watcher-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeLiveConfig : ILiveEntitiesConfig
    {
        private EntitiesConfig _current = new();
        public int SwapCount;
        public EntitiesConfig? LastSwapped;
        public List<EntitiesConfig> AllSwaps { get; } = new();

        public EntitiesConfig Get() => _current;

        public void Swap(EntitiesConfig newConfig)
        {
            Interlocked.Increment(ref SwapCount);
            LastSwapped = newConfig;
            AllSwaps.Add(newConfig);
            _current = newConfig;
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? ConfigChanged;
    }

    /// <summary>
    /// Captures Warning log calls to verify invalid-edit handling.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ConfigFileWatcherService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static string WriteValidYaml(string dir)
    {
        var path = Path.Combine(dir, "entities.yaml");
        File.WriteAllText(path, """
            entities:
              - entity_id: sensor.salon_temperatura
                detectors:
                  - name: hst
                    params:
                      window: "250"
                      n_trees: "25"
                      high_threshold: "0.7"
                      low_threshold: "0.3"
                      min_consecutive: "3"
                      frozen_window: "10"
                      frozen_variance_threshold: "0.001"
            """);
        return path;
    }

    private static string WriteInvalidYaml(string dir)
    {
        var path = Path.Combine(dir, "entities.yaml");
        // Write structurally broken YAML that EntitiesConfigLoader.Validate rejects (null entity).
        File.WriteAllText(path, "entities:\n  -\n");
        return path;
    }

    private static (ConfigFileWatcherService Service, FakeLiveConfig LiveCfg, RecordingLogger Logger)
        BuildService(string entitiesPath)
    {
        var liveCfg = new FakeLiveConfig();
        var settings = new ConnectionSettings { EntitiesPath = entitiesPath };
        var logger = new RecordingLogger();

        var svc = new ConfigFileWatcherService(liveCfg, settings, logger);
        return (svc, liveCfg, logger);
    }

    // ─── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>Valid config → Reload calls Swap exactly once.</summary>
    [Fact]
    public void Reload_ValidConfig_CallsSwapOnce()
    {
        var path = WriteValidYaml(_tempDir);
        var (svc, liveCfg, _) = BuildService(path);

        svc.InternalReload(path);

        Assert.Equal(1, liveCfg.SwapCount);
        Assert.NotNull(liveCfg.LastSwapped);
        Assert.NotEmpty(liveCfg.LastSwapped!.Entities!);
    }

    /// <summary>Invalid YAML (structural error) → Reload logs warning, Swap is never called.</summary>
    [Fact]
    public void Reload_InvalidConfig_LogsWarningAndDoesNotSwap()
    {
        var path = WriteInvalidYaml(_tempDir);
        var (svc, liveCfg, logger) = BuildService(path);

        svc.InternalReload(path);

        Assert.Equal(0, liveCfg.SwapCount);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>Missing file (FileNotFoundException) → Reload logs warning, Swap is never called.</summary>
    [Fact]
    public void Reload_FileMissing_LogsWarningAndDoesNotSwap()
    {
        var path = Path.Combine(_tempDir, "entities.yaml");
        // Do NOT create the file
        var (svc, liveCfg, logger) = BuildService(path);

        svc.InternalReload(path);

        Assert.Equal(0, liveCfg.SwapCount);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Three SimulateRenamedEvent calls within the debounce window → exactly one Reload
    /// (and thus one Swap) fires after the debounce quiet period.
    /// </summary>
    [Fact]
    public async Task SimulateRenamedEvent_ThreeRapidFires_CoalescesToOneSwap()
    {
        var path = WriteValidYaml(_tempDir);
        var (svc, liveCfg, _) = BuildService(path);

        // Use a short debounce (50ms) to keep the test fast
        svc.SetDebounceIntervalMs(50);

        svc.SimulateRenamedEvent(path);
        svc.SimulateRenamedEvent(path);
        svc.SimulateRenamedEvent(path);

        // Wait well past the debounce window
        await Task.Delay(200);

        Assert.Equal(1, liveCfg.SwapCount);
    }

    /// <summary>
    /// A Renamed event for a file that is NOT entities.yaml → no reload, Swap not called.
    /// </summary>
    [Fact]
    public async Task SimulateRenamedEvent_WrongFileName_DoesNotReload()
    {
        var path = WriteValidYaml(_tempDir);
        var (svc, liveCfg, _) = BuildService(path);

        svc.SetDebounceIntervalMs(50);
        // Simulate a rename for a different file
        svc.SimulateRenamedEvent(Path.Combine(_tempDir, "other.yaml"));

        await Task.Delay(200);

        Assert.Equal(0, liveCfg.SwapCount);
    }

    /// <summary>
    /// Null-guard: constructing with null liveCfg throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_NullLiveCfg_Throws()
    {
        var settings = new ConnectionSettings { EntitiesPath = "x.yaml" };
        Assert.Throws<ArgumentNullException>(() =>
            new ConfigFileWatcherService(null!, settings, NullLogger<ConfigFileWatcherService>.Instance));
    }

    /// <summary>
    /// Null-guard: constructing with null settings throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        var liveCfg = new FakeLiveConfig();
        Assert.Throws<ArgumentNullException>(() =>
            new ConfigFileWatcherService(liveCfg, null!, NullLogger<ConfigFileWatcherService>.Instance));
    }

    /// <summary>
    /// Null-guard: constructing with null logger throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var liveCfg = new FakeLiveConfig();
        var settings = new ConnectionSettings { EntitiesPath = "x.yaml" };
        Assert.Throws<ArgumentNullException>(() =>
            new ConfigFileWatcherService(liveCfg, settings, null!));
    }

    /// <summary>
    /// Valid config reload logs an Information entry (ConfigReloadTriggered).
    /// </summary>
    [Fact]
    public void Reload_ValidConfig_LogsInformation()
    {
        var path = WriteValidYaml(_tempDir);
        var (svc, _, logger) = BuildService(path);

        svc.InternalReload(path);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information);
    }
}
