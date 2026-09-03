using Argus.Orchestrator.Detection;
using Argus.Orchestrator.Web;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Status-code and clamping tests for POST /api/sensors/{entityId}/simulate.
///
/// Fully offline — the handler is a static function over (authorized, entityId, body,
/// service), exactly so these decisions are testable without an HTTP server, which is the
/// convention every other endpoint test file here follows.
///
/// Why these particular cases:
///   403 — §7 #16: the TCP-peer check is the ONLY authentication in front of an endpoint that
///         replays up to 5000 points through a real detector on request. If the guard is ever
///         dropped from the handler, this is the test that notices.
///   400 — the lookback literal is forwarded verbatim to a history source that THROWS on a
///         bad duration shape (InfluxDbReader.cs:160). Without the boundary check an operator
///         typo reads as a server fault.
///   clamp — the other half of the §7 #16 defence, and the reason maxPoints is never trusted.
/// </summary>
public class SimulateEndpointTests
{
    private const string KnownEntity = "sensor.load_5m";

    private sealed class RecordingSimulateService : ISimulateService
    {
        public int CallCount { get; private set; }
        public int LastMaxPoints { get; private set; }
        public string? LastLookback { get; private set; }
        public string? LastDetector { get; private set; }

        public Task<SimulateRunResult> RunAsync(
            string entityId, string detector,
            IReadOnlyDictionary<string, string> parameters,
            string lookback, int maxPoints, CancellationToken ct)
        {
            CallCount++;
            LastMaxPoints = maxPoints;
            LastLookback = lookback;
            LastDetector = detector;

            return Task.FromResult(new SimulateRunResult(
                true, null,
                new SimulateSummary(1, 5.0, 24.0, 1.0, 100, 2, DateTimeOffset.UnixEpoch),
                new double[] { 0.1 }, new double[] { 100.0 },
                new[] { DateTimeOffset.UnixEpoch }, 60, 60));
        }
    }

    private static Func<string, bool> Known(params string[] ids)
        => id => ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Unauthorized_Returns403()
    {
        var service = new RecordingSimulateService();

        var outcome = await SimulateEndpoint.HandleAsync(
            authorized: false, KnownEntity, new SimulateRequestDto("rmad", null, "24h", 2000),
            service, Known(KnownEntity), CancellationToken.None);

        Assert.Equal(403, outcome.StatusCode);
        // And the CPU lever was never pulled — a 403 that still ran the replay would be a
        // denial-of-service surface wearing an access-control label.
        Assert.Equal(0, service.CallCount);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("24 h")]
    [InlineData("-1h")]
    [InlineData("24y")]
    [InlineData("1h; drop")]
    public async Task BadLookback_Returns400(string lookback)
    {
        var service = new RecordingSimulateService();

        var outcome = await SimulateEndpoint.HandleAsync(
            true, KnownEntity, new SimulateRequestDto("rmad", null, lookback, 2000),
            service, Known(KnownEntity), CancellationToken.None);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task MaxPoints_ClampedTo5000()
    {
        var service = new RecordingSimulateService();

        var outcome = await SimulateEndpoint.HandleAsync(
            true, KnownEntity, new SimulateRequestDto("rmad", null, "24h", 1_000_000),
            service, Known(KnownEntity), CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(5000, service.LastMaxPoints);
    }

    [Fact]
    public async Task UnknownEntity_Returns404()
    {
        // Neither in the HA snapshot nor in entities.yaml. Answering 200 with an empty chart
        // would make a typo in the URL indistinguishable from a sensor with no history.
        var outcome = await SimulateEndpoint.HandleAsync(
            true, "sensor.nope", new SimulateRequestDto("rmad", null, "24h", 2000),
            new RecordingSimulateService(), Known(KnownEntity), CancellationToken.None);

        Assert.Equal(404, outcome.StatusCode);
    }

    [Fact]
    public async Task NoHistorySource_Returns503()
    {
        var outcome = await SimulateEndpoint.HandleAsync(
            true, KnownEntity, new SimulateRequestDto("rmad", null, "24h", 2000),
            service: null, Known(KnownEntity), CancellationToken.None);

        Assert.Equal(503, outcome.StatusCode);
    }

    [Fact]
    public async Task MissingBody_UsesDocumentedDefaults()
    {
        var service = new RecordingSimulateService();

        var outcome = await SimulateEndpoint.HandleAsync(
            true, KnownEntity, body: null, service, Known(KnownEntity), CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        // B5: 24h is the window every F13 target number is stated in, so it is the only
        // defensible default — a shorter one would quietly make the numbers incomparable.
        Assert.Equal("24h", service.LastLookback);
        Assert.Equal(2000, service.LastMaxPoints);
        Assert.Equal("rmad", service.LastDetector);
    }

    [Fact]
    public void Projection_OmitsSummaryOnFailureAndCarriesNoDetectorInternals()
    {
        // D-07 allowlist: the response is an explicit record. A failed run must not ship a
        // zeroed summary, which reads as "simulated, found nothing" rather than "did not run".
        var failed = new SimulateRunResult(
            false, "Unimplemented",
            new SimulateSummary(0, 0, 0, 0, 0, 0, default),
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<DateTimeOffset>(), 0, 0);

        var dto = SimulateEndpoint.Project(failed);

        Assert.False(dto.Ok);
        Assert.Null(dto.Summary);
        Assert.Equal("Unimplemented", dto.Error);
    }
}
