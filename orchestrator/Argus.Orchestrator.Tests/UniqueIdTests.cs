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
            "argus_sensor_salon_temperatura_anomaly",
            UniqueId.AnomalyId("sensor.salon_temperatura"));
    }

    [Fact]
    public void ScoreId_CorrectFormula()
    {
        Assert.Equal(
            "argus_sensor_salon_temperatura_score",
            UniqueId.ScoreId("sensor.salon_temperatura"));
    }

    [Fact]
    public void AnomalyId_IsDeterministic()
    {
        var first  = UniqueId.AnomalyId("sensor.salon_temperatura");
        var second = UniqueId.AnomalyId("sensor.salon_temperatura");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ScoreId_IsDeterministic()
    {
        var first  = UniqueId.ScoreId("sensor.outdoor_temperature");
        var second = UniqueId.ScoreId("sensor.outdoor_temperature");
        Assert.Equal(first, second);
    }

    /// <summary>
    /// D-G: the detector name must not appear in the entity identity. WHY this is a rule and
    /// not a preference: the state topic argus/{slug}/flag/state has never been detector-scoped,
    /// so an id that IS would mint a brand-new HA entity on every detector change (hst -> rmad)
    /// while the previous one lived on as a retained orphan fed by the same topic. This test
    /// fails the moment anyone reintroduces the detector into the formula.
    /// </summary>
    [Fact]
    public void IdsAreDetectorAgnostic()
    {
        Assert.Equal("argus_sensor_load_5m_anomaly", UniqueId.AnomalyId("sensor.load_5m"));
        Assert.Equal("argus_sensor_load_5m_score", UniqueId.ScoreId("sensor.load_5m"));

        // The legacy (pre-migration) formula stays available for retraction only.
        Assert.Equal(
            "argus_sensor_load_5m_hst_anomaly",
            UniqueId.LegacyAnomalyId("sensor.load_5m", "hst"));
        Assert.Equal(
            "argus_sensor_load_5m_stl_score",
            UniqueId.LegacyScoreId("sensor.load_5m", "stl"));
    }
}
