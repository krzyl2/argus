using System.Text.Json;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// A short-lived, request/response-only HA WebSocket used exclusively for Recorder history
/// queries (D-K): connect → auth → one command per entity → close.
///
/// Exists as an interface purely as a test seam — <see cref="HaWebSocketClient"/> is the single
/// production implementation. History queries deliberately do NOT reuse the live event socket:
/// that socket has no message router (see HaWebSocketClient class remarks), so a request issued
/// after SubscribeStateChangedAsync would consume state_changed frames, and a history response
/// over the 4 MB frame cap would tear down the whole scoring stream.
/// </summary>
internal interface IHaHistoryConnection : IAsyncDisposable
{
    /// <summary>Connects, sends the auth header, and completes the HA auth handshake.</summary>
    Task ConnectAndAuthAsync(Uri uri, string token, CancellationToken ct);

    /// <summary>
    /// Sends history/history_during_period for exactly one entity and returns the raw
    /// <c>result</c> element (a detached clone — safe to read after the response is disposed).
    /// Returns an Undefined element when HA answers with success=false or no result.
    /// </summary>
    Task<JsonElement> GetHistoryAsync(
        string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}
