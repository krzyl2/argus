using Argus.Orchestrator.Config;
using Argus.Orchestrator.Health;
using Argus.Orchestrator.Logging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// IHaEventSource backed by a raw HA WebSocket client (<see cref="HaWebSocketClient"/>).
///
/// Originally implemented with NetDaemon.Client, but the HA add-on must reach HA through the
/// Supervisor proxy <c>ws://supervisor/core/websocket</c>, which requires an
/// <c>Authorization: Bearer</c> header on the WS upgrade. NetDaemon.Client cannot set that header
/// (its WS factory is internal) and direct HA-core access is blocked for add-ons, so the connection
/// is handled by <see cref="HaWebSocketClient"/> instead. The streaming/filtering/health behaviour
/// below is unchanged. (Class name kept for DI + test stability.)
///
/// Responsibilities:
///   - Connects to HA WebSocket using HaUrl + HaToken from ConnectionSettings (token never logged)
///   - Subscribes to state_changed events, filtered to the configured entity set (O(1) HashSet)
///   - On every reconnection (after the first): get_states snapshot (D-07), 60s binary_sensor suppress
///   - First connect: logs unconfigured numeric sensors (UICFG-05)
///   - Reconnect with exponential backoff: 1s → 2s → 4s → … → cap 60s (STRM-01)
/// </summary>
public class NetDaemonHaEventSource : IHaEventSource
{
    // Reconnect backoff constants (STRM-01): starts at 1s, doubles, capped at 60s
    private const int BackoffInitialSeconds = 1;
    private const int BackoffMaxSeconds = 60;

    private readonly ConnectionSettings _settings;
    private readonly EntitiesConfig _entitiesConfig;
    private readonly ReconnectCooldown _cooldown;
    private readonly ArgusHealthSignals _signals;
    private readonly IHaSensorRegistry _sensorRegistry;
    private readonly ILogger<NetDaemonHaEventSource> _logger;

    // Precomputed O(1) lookup set of configured entity_ids
    private readonly HashSet<string> _configuredEntities;

    public NetDaemonHaEventSource(
        ConnectionSettings settings,
        EntitiesConfig entitiesConfig,
        ReconnectCooldown cooldown,
        ArgusHealthSignals signals,
        IHaSensorRegistry registry,
        ILogger<NetDaemonHaEventSource> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _entitiesConfig = entitiesConfig ?? throw new ArgumentNullException(nameof(entitiesConfig));
        _cooldown = cooldown ?? throw new ArgumentNullException(nameof(cooldown));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _sensorRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _configuredEntities = new HashSet<string>(
            entitiesConfig.Entities.Select(e => e.EntityId),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HaReading> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Bounded channel: prevents unbounded queue growth if consumer is slow
        var channel = Channel.CreateBounded<HaReading>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true,
        });

        // Run the HA connection + backoff loop on a background task
        var loopTask = Task.Run(() => RunConnectionLoopAsync(channel.Writer, ct), ct);

        await foreach (var reading in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return reading;
        }

        // Propagate any exception from the background task
        await loopTask; // throws if RunConnectionLoopAsync faulted
    }

    /// <summary>
    /// Outer reconnect loop with exponential backoff (STRM-01).
    /// Handles connect/disconnect and writes HaReadings to the channel.
    /// </summary>
    private async Task RunConnectionLoopAsync(ChannelWriter<HaReading> writer, CancellationToken ct)
    {
        var backoffSeconds = BackoffInitialSeconds;
        var isFirstConnection = true;
        var wsUri = BuildWsUri(_settings.HaUrl);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation(LogEvents.HaListenerStarting,
                        "Connecting to HA WebSocket at {HaUrl}", wsUri);

                    await using var client = new HaWebSocketClient();
                    await client.ConnectAndAuthAsync(wsUri, _settings.HaToken ?? string.Empty, ct)
                        .ConfigureAwait(false);

                    _logger.LogInformation(LogEvents.ChannelEstablished,
                        "Connected and authenticated to HA WebSocket at {HaUrl}", wsUri);

                    // Signal HA connectivity (HEALTH-01 composite health)
                    _signals.HaConnected = true;

                    // Reset backoff on successful connection
                    backoffSeconds = BackoffInitialSeconds;

                    // get_states snapshot must happen BEFORE subscribe (no events interleave).
                    var states = await client.GetStatesAsync(ct).ConfigureAwait(false);

                    // Populate sensor registry on EVERY connect (first + reconnect) — ADR-4: no second WebSocket.
                    _sensorRegistry.UpdateSnapshot(states, _configuredEntities);
                    _logger.LogInformation(LogEvents.SensorRegistryUpdated,
                        "Sensor registry updated: {Count} numeric sensors cached", states.Count(
                            s => double.TryParse(s.State, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out _)));

                    if (isFirstConnection)
                    {
                        // First connect: log unconfigured numeric sensors (UICFG-05)
                        LogDiscoverableSensors(states);
                    }
                    else
                    {
                        // Reconnect: feed current values + start binary_sensor suppression (D-07)
                        _logger.LogInformation("HA reconnect: feeding get_states snapshot (D-07, PITFALL 4)");
                        await FeedStatesAsync(states, writer, ct).ConfigureAwait(false);
                        _cooldown.MarkReconnect(DateTimeOffset.UtcNow);
                        _logger.LogInformation(
                            "ReconnectCooldown started — binary_sensor suppressed for {Seconds}s",
                            ReconnectCooldown.SuppressionWindowSeconds);
                    }

                    isFirstConnection = false;

                    await client.SubscribeStateChangedAsync(ct).ConfigureAwait(false);

                    // Stream state_changed events until the socket closes or CT fires.
                    await client.ReceiveEventsAsync(dto => OnStateChanged(dto, writer), ct)
                        .ConfigureAwait(false);

                    // Clean close (ReceiveEventsAsync returned without throwing): clear the
                    // signal before the next reconnect so HealthPublisherWorker does not report
                    // healthy while HA is down (WR-01, HEALTH-01).
                    _signals.HaConnected = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Clear HA connectivity signal on any connection loss (HEALTH-01)
                    _signals.HaConnected = false;

                    _logger.LogWarning(ex,
                        "HA WebSocket connection lost — backing off {BackoffSeconds}s before reconnect",
                        backoffSeconds);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Exponential backoff capped at 60s (STRM-01, T-05-03)
                    backoffSeconds = Math.Min(backoffSeconds * 2, BackoffMaxSeconds);
                }
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>Feeds a get_states snapshot into the channel (D-07 reconnect snapshot).</summary>
    private async Task FeedStatesAsync(
        IReadOnlyList<HaStateDto> states,
        ChannelWriter<HaReading> writer,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var suppress = _cooldown.IsSuppressed(now);
        var count = 0;

        foreach (var state in states)
        {
            if (TryMap(state.EntityId, state.State, state.LastChangedUtc, _configuredEntities, suppress, out var reading))
            {
                await writer.WriteAsync(reading!, ct).ConfigureAwait(false);
                count++;
            }
        }

        _logger.LogInformation(
            "get_states snapshot fed {Count} configured entities to pipeline", count);
    }

    /// <summary>Maps and forwards a single state_changed new_state (best-effort, non-blocking).</summary>
    private void OnStateChanged(HaStateDto dto, ChannelWriter<HaReading> writer)
    {
        try
        {
            var suppress = _cooldown.IsSuppressed(DateTimeOffset.UtcNow);
            if (TryMap(dto.EntityId, dto.State, dto.LastChangedUtc, _configuredEntities, suppress, out var reading))
            {
                if (!writer.TryWrite(reading!))
                {
                    _logger.LogWarning(
                        "HaReading channel full — dropping event for {EntityId}", dto.EntityId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing state_changed event");
        }
    }

    /// <summary>
    /// Logs discovered numeric sensors (UICFG-05) on the FIRST successful HA connect.
    /// One INFO line per unconfigured numeric sensor, then a total-count line.
    /// </summary>
    private void LogDiscoverableSensors(IReadOnlyList<HaStateDto> states)
    {
        var discoverable = SelectDiscoverableSensors(
            states.Select(s => (s.EntityId, s.State)),
            _configuredEntities);

        foreach (var (entityId, value) in discoverable)
        {
            _logger.LogInformation(LogEvents.DiscoveredSensorsLogged,
                "Unconfigured numeric sensor: {EntityId} = {Value}", entityId, value);
        }

        _logger.LogInformation(LogEvents.DiscoveredSensorsLogged,
            "Startup sensor discovery: {Count} unconfigured numeric sensors found", discoverable.Count);
    }

    /// <summary>
    /// Pure static selector for UICFG-05: returns all numeric sensors not already in configuredEntities.
    /// A state qualifies when its value parses as double (invariant culture) and its entity_id is
    /// not in the configured set. Internal for unit testing without a live HA connection.
    /// </summary>
    internal static IReadOnlyList<(string EntityId, double Value)> SelectDiscoverableSensors(
        IEnumerable<(string EntityId, string? State)> states,
        HashSet<string> configuredEntities)
    {
        var result = new List<(string, double)>();
        foreach (var (entityId, state) in states)
        {
            if (configuredEntities.Contains(entityId))
                continue;
            if (!double.TryParse(state, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                continue;
            result.Add((entityId, value));
        }
        return result;
    }

    /// <summary>
    /// Maps a raw HA state to an HaReading.
    /// Returns false (and null reading) if:
    ///   - entity_id is not in the configured set, or
    ///   - state value is not parseable as double (e.g. "unavailable", "unknown") (T-05-01)
    /// This method is static/internal so it can be unit-tested without a live HA connection.
    /// </summary>
    internal static bool TryMap(
        string entityId,
        string? stateValue,
        DateTime lastChanged,
        HashSet<string> configuredEntities,
        bool suppressBinarySensor,
        out HaReading? reading)
    {
        reading = null;

        // Entity filter — O(1) HashSet lookup
        if (!configuredEntities.Contains(entityId))
            return false;

        // Numeric validation — invariant culture (T-05-01)
        if (!double.TryParse(stateValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return false;

        reading = new HaReading(
            EntityId: entityId,
            Value: value,
            LastChanged: new DateTimeOffset(lastChanged, TimeSpan.Zero),
            SuppressBinarySensor: suppressBinarySensor);

        return true;
    }

    /// <summary>
    /// Builds the WebSocket URI from the configured HA URL. Converts http/https → ws/wss,
    /// preserves an explicit port, and defaults a root path to /api/websocket (direct HA core).
    /// The add-on supplies ws://supervisor/core/websocket (Supervisor proxy) verbatim.
    /// </summary>
    private static Uri BuildWsUri(string? haUrl)
    {
        var raw = string.IsNullOrEmpty(haUrl) ? "ws://supervisor/core/websocket" : haUrl;
        var uri = new Uri(raw, UriKind.Absolute);
        var scheme = uri.Scheme is "https" or "wss" ? "wss" : "ws";
        var path = uri.AbsolutePath is "" or "/" ? "/api/websocket" : uri.AbsolutePath;
        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return new Uri($"{scheme}://{uri.Host}{portPart}{path}");
    }
}
