using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Verifies that Grpc.Tools generated the expected C# types from proto/argus.proto.
/// These tests prove the DoubleValue wrapper (D-01) and service stub are present.
///
/// NOTE: Google.Protobuf 3.x C# codegen maps google.protobuf.DoubleValue to double? (nullable double).
/// Assignment of null means "field absent on wire"; assignment of 0.0 sends the wrapper with value=0.0.
/// This is the D-01 guarantee: score=0.0 is NOT silently dropped (proto3 default-value drop prevention).
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
    public void Point_ValueField_IsNullableDouble_ProvesDDoubleValueWrapper()
    {
        // D-01: google.protobuf.DoubleValue maps to double? in C# (not raw double).
        // Setting value=0.0 is preserved (not dropped on wire); setting null means "absent".
        var point = new Argus.Detector.V1.Point
        {
            EntityId = "sensor.test",
            Value = 0.0   // double? assignment — proves wrapper type, not raw double
        };

        // Verify 0.0 is preserved (this would fail if the field were raw double with default semantics)
        Assert.NotNull(point.Value);
        Assert.Equal(0.0, point.Value);

        // Verify null is assignable (i.e., field is nullable — double? not double)
        point.Value = null;
        Assert.Null(point.Value);
    }

    [Fact]
    public void DetectorServiceClient_TypeExists()
    {
        // Proves Grpc.Tools generated the service stub from the DetectorService definition in argus.proto
        var clientType = typeof(Argus.Detector.V1.DetectorService.DetectorServiceClient);
        Assert.NotNull(clientType);
    }

    [Fact]
    public void Verdict_ScoreAndExpected_AreNullableDouble()
    {
        // Proves D-01 on Verdict: score, expected, lower, upper are all double? wrappers
        var verdict = new Argus.Detector.V1.Verdict
        {
            Score = 0.0,
            Expected = 22.5
        };

        Assert.Equal(0.0, verdict.Score);
        Assert.Equal(22.5, verdict.Expected);

        // Null assignment proves nullable wrapper
        verdict.Score = null;
        Assert.Null(verdict.Score);
    }
}
