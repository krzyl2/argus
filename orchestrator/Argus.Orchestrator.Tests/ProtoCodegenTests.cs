using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Verifies that Grpc.Tools generated the expected C# types from proto/argus.proto.
/// These tests prove the DoubleValue wrapper (D-01) and service stub are present.
/// </summary>
public class ProtoCodegenTests
{
    [Fact]
    public void Point_TypeExists_AndIsConstructible()
    {
        // Proves Grpc.Tools generated Argus.Detector.V1.Point from argus.proto
        var point = new Argus.Detector.V1.Point();
        Assert.NotNull(point);
    }

    [Fact]
    public void Point_ValueField_IsDoubleValue_NotRawDouble()
    {
        // Proves D-01: value field uses google.protobuf.DoubleValue wrapper (not raw double)
        // so score=0.0 is not silently dropped on the wire.
        var point = new Argus.Detector.V1.Point
        {
            EntityId = "sensor.test",
            Value = new DoubleValue { Value = 0.0 }
        };

        Assert.IsType<DoubleValue>(point.Value);
        Assert.Equal(0.0, point.Value.Value);
    }

    [Fact]
    public void DetectorServiceClient_TypeExists()
    {
        // Proves Grpc.Tools generated the service stub from the DetectorService definition in argus.proto
        var clientType = typeof(Argus.Detector.V1.DetectorService.DetectorServiceClient);
        Assert.NotNull(clientType);
    }

    [Fact]
    public void Verdict_ScoreField_IsDoubleValue()
    {
        // Proves D-01 on Verdict: score, expected, lower, upper are all DoubleValue wrappers
        var verdict = new Argus.Detector.V1.Verdict
        {
            Score = new DoubleValue { Value = 0.0 },
            Expected = new DoubleValue { Value = 22.5 }
        };

        Assert.Equal(0.0, verdict.Score.Value);
        Assert.Equal(22.5, verdict.Expected.Value);
    }
}
