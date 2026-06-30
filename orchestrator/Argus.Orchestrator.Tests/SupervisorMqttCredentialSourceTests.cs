using System.Net;
using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Unit tests for SupervisorMqttCredentialSource.
/// Uses a faked HttpMessageHandler — no live Supervisor or broker needed.
/// </summary>
public class SupervisorMqttCredentialSourceTests
{
    private static ConnectionSettings FallbackSettings() => new()
    {
        MqttHost = "fallback-host",
        MqttPort = 9999,
        MqttUser = "fallback-user",
        MqttPassword = "fallback-pass",
    };

    private const string SupervisorJsonOk = """
        {
          "result": "ok",
          "data": {
            "host": "supervisor-host",
            "port": 1883,
            "username": "supervisor-user",
            "password": "supervisor-pass"
          }
        }
        """;

    [Fact]
    public async Task GetAsync_WithToken_ReturnsParsedCredentials()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, SupervisorJsonOk);
        var source = new SupervisorMqttCredentialSource(
            new HttpClient(handler),
            FallbackSettings(),
            NullLogger<SupervisorMqttCredentialSource>.Instance,
            () => "test-token");

        var creds = await source.GetAsync(CancellationToken.None);

        Assert.Equal("supervisor-host", creds.Host);
        Assert.Equal(1883, creds.Port);
        Assert.Equal("supervisor-user", creds.User);
        Assert.Equal("supervisor-pass", creds.Password);
    }

    [Fact]
    public async Task GetAsync_WithToken_SendsBearerHeaderToSupervisorEndpoint()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, SupervisorJsonOk);
        var source = new SupervisorMqttCredentialSource(
            new HttpClient(handler),
            FallbackSettings(),
            NullLogger<SupervisorMqttCredentialSource>.Instance,
            () => "my-secret-token");

        await source.GetAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://supervisor/services/mqtt", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("my-secret-token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetAsync_WithoutToken_ReturnsFallbackCredentials_NoHttpCall()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, SupervisorJsonOk);
        var source = new SupervisorMqttCredentialSource(
            new HttpClient(handler),
            FallbackSettings(),
            NullLogger<SupervisorMqttCredentialSource>.Instance,
            () => null);   // no SUPERVISOR_TOKEN

        var creds = await source.GetAsync(CancellationToken.None);

        Assert.Equal("fallback-host", creds.Host);
        Assert.Equal(9999, creds.Port);
        Assert.Equal("fallback-user", creds.User);
        Assert.Equal("fallback-pass", creds.Password);
        Assert.Equal(0, handler.CallCount); // HTTP handler never invoked
    }

    [Fact]
    public async Task GetAsync_WithEmptyToken_ReturnsFallbackCredentials_NoHttpCall()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, SupervisorJsonOk);
        var source = new SupervisorMqttCredentialSource(
            new HttpClient(handler),
            FallbackSettings(),
            NullLogger<SupervisorMqttCredentialSource>.Instance,
            () => "");   // empty token treated as absent

        var creds = await source.GetAsync(CancellationToken.None);

        Assert.Equal("fallback-host", creds.Host);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_WhenHttpThrows_ReturnsFallbackCredentials()
    {
        var handler = new ThrowingHttpHandler();
        var source = new SupervisorMqttCredentialSource(
            new HttpClient(handler),
            FallbackSettings(),
            NullLogger<SupervisorMqttCredentialSource>.Instance,
            () => "test-token");

        var creds = await source.GetAsync(CancellationToken.None);

        Assert.Equal("fallback-host", creds.Host);
        Assert.Equal(9999, creds.Port);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public FakeHttpHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body)
            });
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated network failure");
    }
}
