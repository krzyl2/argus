using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// A configured-entity-agnostic HA state snapshot row from get_states / state_changed.
/// </summary>
internal sealed record HaStateDto(
    string EntityId, string? State, DateTime LastChangedUtc,
    string? UnitOfMeasurement, string? FriendlyName);

/// <summary>
/// Minimal Home Assistant WebSocket client built on a raw <see cref="ClientWebSocket"/>.
///
/// Replaces NetDaemon.Client for the connection because NetDaemon.Client cannot set the
/// HTTP <c>Authorization</c> header that the HA Supervisor proxy (<c>ws://supervisor/core/websocket</c>)
/// requires on the WS upgrade — its IWebSocketClientFactory is <c>internal</c>. This client sets
/// <c>Authorization: Bearer &lt;token&gt;</c> on the upgrade (required by the Supervisor proxy,
/// ignored by HA core's direct /api/websocket) and performs the in-protocol HA auth handshake.
///
/// Usage is strictly sequential per connection: ConnectAndAuth → (GetStates)* → Subscribe →
/// ReceiveEvents. All reads share the one socket, so no message router is needed.
/// The HA token is never logged.
/// </summary>
internal sealed class HaWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _id;

    /// <summary>Connects, sends the auth header, and completes the HA auth handshake.</summary>
    public async Task ConnectAndAuthAsync(Uri uri, string token, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(token))
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        // HA sends {"type":"auth_required"} first.
        using (await ReceiveMessageAsync(ct).ConfigureAwait(false)) { }

        await SendAsync(new { type = "auth", access_token = token }, ct).ConfigureAwait(false);

        using var authResult = await ReceiveMessageAsync(ct).ConfigureAwait(false);
        var type = authResult.RootElement.GetProperty("type").GetString();
        if (type != "auth_ok")
        {
            var msg = authResult.RootElement.TryGetProperty("message", out var m) ? m.GetString() : type;
            throw new InvalidOperationException($"HA WebSocket authentication failed: {msg}");
        }
    }

    /// <summary>Sends get_states and returns the current states. Call before Subscribe.</summary>
    public async Task<IReadOnlyList<HaStateDto>> GetStatesAsync(CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        await SendAsync(new { id, type = "get_states" }, ct).ConfigureAwait(false);

        while (true)
        {
            using var doc = await ReceiveMessageAsync(ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!IsResultFor(root, id))
                continue;

            var list = new List<HaStateDto>();
            if (root.TryGetProperty("success", out var s) && s.GetBoolean()
                && root.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var st in arr.EnumerateArray())
                {
                    if (!st.TryGetProperty("entity_id", out var eid) || eid.GetString() is not { } entityId)
                        continue;
                    var state = st.TryGetProperty("state", out var stv) ? stv.GetString() : null;
                    string? unit = null, friendlyName = null;
                    if (st.TryGetProperty("attributes", out var attrs))
                    {
                        if (attrs.TryGetProperty("unit_of_measurement", out var u)) unit = u.GetString();
                        if (attrs.TryGetProperty("friendly_name", out var fn)) friendlyName = fn.GetString();
                    }
                    list.Add(new HaStateDto(entityId, state, ParseUtc(st, "last_changed"), unit, friendlyName));
                }
            }
            return list;
        }
    }

    /// <summary>Subscribes to state_changed events; returns once HA confirms the subscription.</summary>
    public async Task SubscribeStateChangedAsync(CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        await SendAsync(new { id, type = "subscribe_events", event_type = "state_changed" }, ct).ConfigureAwait(false);

        while (true)
        {
            using var doc = await ReceiveMessageAsync(ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!IsResultFor(root, id))
                continue;
            if (!(root.TryGetProperty("success", out var s) && s.GetBoolean()))
                throw new InvalidOperationException("HA subscribe_events(state_changed) was rejected");
            return;
        }
    }

    /// <summary>
    /// Reads the event stream and invokes <paramref name="onState"/> for each state_changed
    /// new_state. Returns when HA closes the stream cleanly; throws on socket error or close
    /// frame so the caller can back off and reconnect.
    /// </summary>
    public async Task ReceiveEventsAsync(Action<HaStateDto> onState, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var doc = await ReceiveMessageAsync(ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var t) || t.GetString() != "event")
                continue;
            if (!root.TryGetProperty("event", out var ev)
                || !ev.TryGetProperty("event_type", out var et) || et.GetString() != "state_changed"
                || !ev.TryGetProperty("data", out var data)
                || !data.TryGetProperty("new_state", out var ns) || ns.ValueKind != JsonValueKind.Object)
                continue;

            var entityId = ns.TryGetProperty("entity_id", out var eid) ? eid.GetString()
                : data.TryGetProperty("entity_id", out var eid2) ? eid2.GetString() : null;
            if (entityId is null)
                continue;

            var state = ns.TryGetProperty("state", out var stv) ? stv.GetString() : null;
            string? unit = null, friendlyName = null;
            if (ns.TryGetProperty("attributes", out var attrs))
            {
                if (attrs.TryGetProperty("unit_of_measurement", out var u)) unit = u.GetString();
                if (attrs.TryGetProperty("friendly_name", out var fn)) friendlyName = fn.GetString();
            }
            onState(new HaStateDto(entityId, state, ParseUtc(ns, "last_changed"), unit, friendlyName));
        }
    }

    private static bool IsResultFor(JsonElement root, int id) =>
        root.TryGetProperty("id", out var idEl)
        && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id
        && root.TryGetProperty("type", out var t) && t.GetString() == "result";

    private static DateTime ParseUtc(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto.UtcDateTime;
        }
        return DateTime.UtcNow;
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReceiveMessageAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely,
                    $"HA closed the WebSocket ({result.CloseStatus}: {result.CloseStatusDescription})");
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* best-effort close */ }
        _ws.Dispose();
    }
}
