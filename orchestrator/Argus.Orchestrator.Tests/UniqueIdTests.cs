using Argus.Orchestrator.Mqtt;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class UniqueIdTests
{
    [Fact]
    public void Slug_DotReplaceWithUnderscore()
    {
        Assert.Equal("sensor_salon_temperatura", UniqueId.Slug("sensor.salon_temperatura"));
    }

    [Fact]
    public void AnomalyId_CorrectFormula()
    {
        Assert.Equal(
            "argus_sensor_salon_temperatura_hst_anomaly",
            UniqueId.AnomalyId("sensor.salon_temperatura", "hst"));
    }

    [Fact]
    public void ScoreId_CorrectFormula()
    {
        Assert.Equal(
            "argus_sensor_salon_temperatura_hst_score",
            UniqueId.ScoreId("sensor.salon_temperatura", "hst"));
    }

    [Fact]
    public void AnomalyId_IsDeterministic()
    {
        var first  = UniqueId.AnomalyId("sensor.salon_temperatura", "hst");
        var second = UniqueId.AnomalyId("sensor.salon_temperatura", "hst");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ScoreId_IsDeterministic()
    {
        var first  = UniqueId.ScoreId("sensor.outdoor_temperature", "hst");
        var second = UniqueId.ScoreId("sensor.outdoor_temperature", "hst");
        Assert.Equal(first, second);
    }
}
