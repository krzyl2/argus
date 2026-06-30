using System.Net.Http.Headers;
using System.Text.Json;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Logging;

namespace Argus.Orchestrator.Mqtt;

/// <summary>
/// Fetches MQTT credentials from the HA Supervisor API (GET /services/mqtt) on every call.
/// Falls back to ConnectionSettings (ARGUS_MQTT_* env vars) when SUPERVISOR_TOKEN is absent
/// or the API call fails, preserving v1 docker-compose / remote-detector behavior.
///
/// Security: token and password values are never written to logs (T-03-03 mitigated).
/// The Supervisor URL is plain http:// because it runs on the trusted internal add-on
/// network proxy — this is HA platform contract, not external exposure (T-03-06 accepted).
/// </summary>
public sealed class SupervisorMqttCredentialSource : IMqttCredentialSource
{
    private const string SupervisorMqttUrl = "http://supervisor/services/mqtt";

    private readonly HttpClient _http;
    private readonly ConnectionSettings _settings;
    private readonly ILogger<SupervisorMqttCredentialSource> _logger;
    private readonly Func<string?> _tokenAccessor;

    /// <param name="http">HttpClient used for the Supervisor API call (injected for testability).</param>
    /// <param name="settings">Fallback env-var credentials (ARGUS_MQTT_*).</param>
    /// <param name="logger">Logger — credential values are never written here.</param>
    /// <param name="tokenAccessor">Returns the Supervisor bearer token; defaults to reading SUPERVISOR_TOKEN env var.</param>
    public SupervisorMqttCredentialSource(
        HttpClient http,
        ConnectionSettings settings,
        ILogger<SupervisorMqttCredentialSource> logger,
        Func<string?>? tokenAccessor = null)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _tokenAccessor = tokenAccessor ?? (() => Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN"));
    }

    /// <inheritdoc/>
    public async Task<MqttCredentials> GetAsync(CancellationToken ct)
    {
        var token = _tokenAccessor();
        if (string.IsNullOrEmpty(token))
            return Fallback();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SupervisorMqttUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var host = data.GetProperty("host").GetString();
            var port = data.GetProperty("port").GetInt32();
            var user = data.GetProperty("username").GetString();
            var password = data.GetProperty("password").GetString();

            // Log host/port only — never user, password, or token (T-03-03)
            _logger.LogInformation(LogEvents.MqttCredentialsRefreshed,
                "MQTT credentials fetched from Supervisor API: {Host}:{Port}", host, port);

            return new MqttCredentials(host, port, user, password);
        }
        catch (Exception ex)
        {
            // Log exception + endpoint but no secret values (T-03-03)
            _logger.LogWarning(LogEvents.MqttCredentialsRefreshed, ex,
                "Failed to fetch MQTT credentials from {Url} — using env-var fallback",
                SupervisorMqttUrl);
            return Fallback();
        }
    }

    private MqttCredentials Fallback() => new(
        _settings.MqttHost,
        _settings.MqttPort,
        _settings.MqttUser,
        _settings.MqttPassword);
}
