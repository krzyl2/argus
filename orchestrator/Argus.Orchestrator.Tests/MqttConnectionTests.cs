using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class MqttConnectionTests
{
    private static ConnectionSettings MakeSettings() => new()
    {
        MqttHost = "localhost",
        MqttPort = 1883,
        MqttUser = "testuser",
        MqttPassword = "testpass",
    };

    private static MqttConnection MakeConn(FakeCredentialSource? source = null) =>
        new(source ?? new FakeCredentialSource(MakeSettings()), NullLogger<MqttConnection>.Instance);

    // ── LWT assertions (now via BuildConnectOptionsAsync; no live broker) ──

    [Fact]
    public async Task BuildConnectOptionsAsync_WillTopic_EndsWithAvailability()
    {
        var conn = MakeConn();
        var opts = await conn.BuildConnectOptionsAsync(CancellationToken.None);
        Assert.EndsWith("/availability", opts.WillTopic);
    }

    [Fact]
    public async Task BuildConnectOptionsAsync_WillPayload_IsOffline()
    {
        var conn = MakeConn();
        var opts = await conn.BuildConnectOptionsAsync(CancellationToken.None);
        var payload = System.Text.Encoding.UTF8.GetString(opts.WillPayload!);
        Assert.Equal("offline", payload);
    }

    [Fact]
    public async Task BuildConnectOptionsAsync_WillRetain_IsTrue()
    {
        var conn = MakeConn();
        var opts = await conn.BuildConnectOptionsAsync(CancellationToken.None);
        Assert.True(opts.WillRetain);
    }

    [Fact]
    public async Task BuildConnectOptionsAsync_LwtIsConfigured_BeforeConnectAttempt()
    {
        // Verifies LWT is in the options object returned before any ConnectAsync call (PITFALL 6).
        var conn = MakeConn();
        var opts = await conn.BuildConnectOptionsAsync(CancellationToken.None);
        Assert.NotNull(opts.WillTopic);
        Assert.NotEmpty(opts.WillTopic);
    }

    // ── Per-attempt credential refetch assertions (SUPV-03) ──

    [Fact]
    public async Task BuildConnectOptionsAsync_InvokesCredentialSourceOnEveryCall()
    {
        var source = new FakeCredentialSource(MakeSettings());
        var conn = MakeConn(source);

        await conn.BuildConnectOptionsAsync(CancellationToken.None);
        await conn.BuildConnectOptionsAsync(CancellationToken.None);
        await conn.BuildConnectOptionsAsync(CancellationToken.None);

        // Each call must produce a fresh fetch — never return a cached options object
        Assert.Equal(3, source.CallCount);
    }

    [Fact]
    public async Task BuildConnectOptionsAsync_DistinctCalls_ProduceSeparateOptionsObjects()
    {
        var source = new FakeCredentialSource(MakeSettings());
        var conn = MakeConn(source);

        var opts1 = await conn.BuildConnectOptionsAsync(CancellationToken.None);
        var opts2 = await conn.BuildConnectOptionsAsync(CancellationToken.None);

        // Not the same cached reference (SUPV-03 — no cross-attempt state sharing)
        Assert.NotSame(opts1, opts2);
        // Both carry the LWT (RES-01)
        Assert.Equal("offline", System.Text.Encoding.UTF8.GetString(opts1.WillPayload!));
        Assert.Equal("offline", System.Text.Encoding.UTF8.GetString(opts2.WillPayload!));
    }

    // ── StatePublisher topic-shape tests (unchanged from v1) ──

    [Fact]
    public void StatePublisher_FlagTopic_Correct()
    {
        var publisher = new StatePublisher();
        Assert.Equal("argus/sensor_salon_temperatura/flag/state",
            publisher.FlagTopic("sensor.salon_temperatura"));
    }

    [Fact]
    public void StatePublisher_ScoreTopic_Correct()
    {
        var publisher = new StatePublisher();
        Assert.Equal("argus/sensor_salon_temperatura/score/state",
            publisher.ScoreTopic("sensor.salon_temperatura"));
    }

    [Fact]
    public void StatePublisher_BridgeAvailabilityTopic_Constant()
    {
        Assert.Equal("argus/bridge/availability", StatePublisher.BridgeAvailabilityTopic);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal sealed class FakeCredentialSource : IMqttCredentialSource
    {
        private readonly MqttCredentials _creds;
        public int CallCount { get; private set; }

        public FakeCredentialSource(ConnectionSettings settings)
        {
            _creds = new MqttCredentials(
                settings.MqttHost,
                settings.MqttPort,
                settings.MqttUser,
                settings.MqttPassword);
        }

        public Task<MqttCredentials> GetAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_creds);
        }
    }
}
