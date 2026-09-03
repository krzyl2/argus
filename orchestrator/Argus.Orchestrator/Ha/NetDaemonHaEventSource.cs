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

    // Snapshot pass labels (WS4 D1 probe) — they are grep keys in the operator's log, so they
    // are constants rather than inline literals.
    internal const string RegistryPassInitial = "initial";
    internal const string RegistryPassSettle = "settle";
    internal const string RegistryPassReconnect = "reconnect";

    private readonly ConnectionSettings _settings;
    private readonly ILiveEntitiesConfig _liveConfig;
    private readonly ReconnectCooldown _cooldown;
    private readonly ArgusHealthSignals _signals;
    private readonly IHaSensorRegistry _sensorRegistry;
    private readonly ILogger<NetDaemonHaEventSource> _logger;

    // Live O(1) lookup set of configured entity_ids.
    // Single writer: ConfigChanged handler (volatile reference swap — mirrors HaSensorRegistry pattern).
    // Readers (OnStateChanged, FeedStatesAsync, LogDiscoverableSensors) read the current reference.
    private volatile HashSet<string> _configuredEntities;

    public NetDaemonHaEventSource(
        ConnectionSettings settings,
        ILiveEntitiesConfig liveConfig,
        ReconnectCooldown cooldown,
        ArgusHealthSignals signals,
        IHaSensorRegistry registry,
        ILogger<NetDaemonHaEventSource> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _liveConfig = liveConfig ?? throw new ArgumentNullException(nameof(liveConfig));
        _cooldown = cooldown ?? throw new ArgumentNullException(nameof(cooldown));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _sensorRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _configuredEntities = BuildEntitySet(_liveConfig.Get());

        // CFG-04: rebuild the filter set on every config swap so new entities are
        // accepted immediately without restarting the event source.
        _liveConfig.ConfigChanged += (_, _) =>
            _configuredEntities = BuildEntitySet(_liveConfig.Get());
    }

    /// <summary>Builds an OrdinalIgnoreCase HashSet of entity_ids from <paramref name="cfg"/>.</summary>
    private static HashSet<string> BuildEntitySet(EntitiesConfig cfg) =>
        new(cfg.Entities.Select(e => e.EntityId), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test seam: returns the current entity filter set (the volatile reference snapshot).
    /// Internal so that unit tests (via InternalsVisibleTo) can assert CFG-04 live-filter semantics.
    /// </summary>
    internal HashSet<string> InternalConfiguredEntities => _configuredEntities;

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

                    // Snapshot passes must happen BEFORE subscribe (no events interleave) — see
                    // RunSnapshotPassesAsync for why the ordering is a correctness constraint and
                    // not a preference.
                    var wasFirstConnection = isFirstConnection;
                    await RunSnapshotPassesAsync(
                        isFirstConnection: wasFirstConnection,
                        settleSeconds: _settings.RegistrySettleSeconds,
                        getStates: c => client.GetStatesAsync(c),
                        onSnapshot: async (snapshot, pass, c) =>
                        {
                            // Area/entity registries change far less often than sensor values — fetched
                            // once per connect (first + reconnect, registries can change while
                            // disconnected), right after GetStatesAsync and before UpdateSnapshot (SRCH-02/03).
                            var entityAreaNames = await BuildEntityAreaNamesAsync(client, c).ConfigureAwait(false);

                            // Populate sensor registry on EVERY connect (first + reconnect) — ADR-4: no second WebSocket.
                            // WS5/D-K scope note: ADR-4 forbids a second PERSISTENT event channel. Recorder
                            // history queries (HaRecorderHistorySource) open a short-lived, request/response-only
                            // socket that never subscribes to anything and is closed after the last command, so
                            // it creates no second stream and cannot consume state_changed frames. Do not
                            // "restore" ADR-4 by moving those queries onto this socket: it has no message router
                            // (HaWebSocketClient.cs:35-37), so a history response would be read as an event frame,
                            // and a >4 MB response would tear down live scoring for every entity.
                            _sensorRegistry.UpdateSnapshot(snapshot, _configuredEntities, entityAreaNames);
                            LogRegistryPass(snapshot, pass);
                            LogGhostEntities();
                        },
                        afterSnapshots: async (snapshot, c) =>
                        {
                            if (wasFirstConnection)
                            {
                                // First connect: log unconfigured numeric sensors (UICFG-05)
                                LogDiscoverableSensors(snapshot);
                            }
                            else
                            {
                                // Reconnect: feed current values + start binary_sensor suppression (D-07)
                                _logger.LogInformation("HA reconnect: feeding get_states snapshot (D-07, PITFALL 4)");
                                await FeedStatesAsync(snapshot, writer, c).ConfigureAwait(false);
                                _cooldown.MarkReconnect(DateTimeOffset.UtcNow);
                                _logger.LogInformation(
                                    "ReconnectCooldown started — binary_sensor suppressed for {Seconds}s",
                                    ReconnectCooldown.SuppressionWindowSeconds);
                            }
                        },
                        subscribe: c => client.SubscribeStateChangedAsync(c),
                        delay: (d, c) => Task.Delay(d, c),
                        ct: ct).ConfigureAwait(false);

                    isFirstConnection = false;

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

    /// <summary>
    /// Runs the get_states passes for ONE connection and then subscribes (WS4/F10).
    ///
    /// Ordering here is a correctness constraint, not a preference: <see cref="HaWebSocketClient"/>
    /// has no message router (see its class docs), so a get_states issued AFTER
    /// <c>subscribe_events</c> would read <c>state_changed</c> frames as its own command reply.
    /// Every snapshot pass must therefore complete before the subscription opens.
    ///
    /// On the FIRST connection only, and only when <paramref name="settleSeconds"/> is above zero,
    /// a SECOND snapshot is taken after a delay. At add-on boot several HA integrations are still
    /// loading and report <c>unknown</c>/<c>unavailable</c>; with a connect-only snapshot those
    /// entities were invisible until the next reconnect — F10's leading hypothesis (H1).
    ///
    /// Delegate-based so the ordering contract is testable without a live socket.
    /// </summary>
    internal static async Task RunSnapshotPassesAsync(
        bool isFirstConnection,
        int settleSeconds,
        Func<CancellationToken, Task<IReadOnlyList<HaStateDto>>> getStates,
        Func<IReadOnlyList<HaStateDto>, string, CancellationToken, Task> onSnapshot,
        Func<IReadOnlyList<HaStateDto>, CancellationToken, Task> afterSnapshots,
        Func<CancellationToken, Task> subscribe,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        var states = await getStates(ct).ConfigureAwait(false);
        await onSnapshot(states, isFirstConnection ? RegistryPassInitial : RegistryPassReconnect, ct)
            .ConfigureAwait(false);

        if (isFirstConnection && settleSeconds > 0)
        {
            await delay(TimeSpan.FromSeconds(settleSeconds), ct).ConfigureAwait(false);
            states = await getStates(ct).ConfigureAwait(false);
            await onSnapshot(states, RegistryPassSettle, ct).ConfigureAwait(false);
        }

        // Discovery logging / reconnect feed run on the FRESHEST snapshot — after settle, an
        // entity that was `unknown` at connect now has a real value to feed.
        await afterSnapshots(states, ct).ConfigureAwait(false);

        await subscribe(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// D1 probe for F10: three counters, not one. "157 numeric sensors cached" cannot distinguish
    /// "HA only told us about 157" from "HA told us about 403 and 246 were non-numeric at that
    /// instant" — and those two point at completely different causes (H3 vs H1). Splitting the
    /// counters is what makes the diagnosis falsifiable from a log line.
    /// </summary>
    private void LogRegistryPass(IReadOnlyList<HaStateDto> states, string pass)
    {
        var numeric = states.Count(s =>
            double.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out _));

        _logger.LogInformation(LogEvents.SensorRegistryUpdated,
            "Sensor registry updated: {Numeric} numeric / {Total} states / {NonNumeric} non-numeric ({Pass} pass)",
            numeric, states.Count, states.Count - numeric, pass);
    }

    /// <summary>
    /// Fail-loud line for F9: an entity Argus is SCORING that the registry cannot see. That state
    /// used to be silent — <c>sensor.zamrazarkapiwnica_power</c> was scored at 0.996 while being
    /// absent from the picker, so it could be neither inspected nor untracked from the UI.
    /// </summary>
    private void LogGhostEntities()
    {
        var known = _sensorRegistry.GetAll()
            .Select(e => e.EntityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in _configuredEntities)
        {
            if (known.Contains(id))
                continue;

            _logger.LogWarning(LogEvents.SensorRegistryGhost,
                "Tracked entity {EntityId} is absent from the HA snapshot — scored but not listed by HA",
                id);
        }
    }

    /// <summary>
    /// Joins config/area_registry/list + config/entity_registry/list into an
    /// entity_id -> area name map (SRCH-02/03). Entity-only area_id + domain fallback for v1
    /// (RESEARCH.md Pitfall 3/Open Question 2) — device_registry-inherited area is NOT resolved
    /// this phase. Degrades safely to an empty map on any WS/parsing failure so area enrichment
    /// never blocks the connect loop.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string?>> BuildEntityAreaNamesAsync(
        HaWebSocketClient client, CancellationToken ct)
    {
        try
        {
            var areas = await client.GetAreaRegistryAsync(ct).ConfigureAwait(false);
            var entities = await client.GetEntityRegistryAsync(ct).ConfigureAwait(false);

            var areaNamesById = areas
                .Where(a => !string.IsNullOrEmpty(a.AreaId))
                .ToDictionary(a => a.AreaId, a => a.Name, StringComparer.OrdinalIgnoreCase);

            return entities
                .Where(e => !string.IsNullOrEmpty(e.EntityId) && !string.IsNullOrEmpty(e.AreaId))
                .ToDictionary(
                    e => e.EntityId,
                    e => areaNamesById.TryGetValue(e.AreaId!, out var name) ? name : null,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HA area/entity registry enrichment failed — falling back to no area names");
            return new Dictionary<string, string?>();
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
            var configured = _configuredEntities;

            // WS4/F10: feed the registry BEFORE the configured-entity filter. The state_changed
            // subscription is global, so every entity that ever changes state becomes pickable
            // without a second WebSocket (ADR-4) — that is the whole mechanism by which an entity
            // missing from the boot snapshot is recovered.
            if (_sensorRegistry.Upsert(dto, configured.Contains(dto.EntityId)))
            {
                _logger.LogDebug(LogEvents.SensorRegistryUpserted,
                    "Sensor registry discovered {EntityId} from state_changed (absent from snapshot)",
                    dto.EntityId);
            }

            var suppress = _cooldown.IsSuppressed(DateTimeOffset.UtcNow);
            if (TryMap(dto.EntityId, dto.State, dto.LastChangedUtc, configured, suppress, out var reading))
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
    /// Internal (not private) so HaRecorderHistorySource resolves the SAME endpoint from the same
    /// ConnectionSettings — two spellings of the HA URL would be two different failure modes.
    /// </summary>
    internal static Uri BuildWsUri(string? haUrl)
    {
        var raw = string.IsNullOrEmpty(haUrl) ? "ws://supervisor/core/websocket" : haUrl;
        var uri = new Uri(raw, UriKind.Absolute);
        var scheme = uri.Scheme is "https" or "wss" ? "wss" : "ws";
        var path = uri.AbsolutePath is "" or "/" ? "/api/websocket" : uri.AbsolutePath;
        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return new Uri($"{scheme}://{uri.Host}{portPart}{path}");
    }
}
