using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Xunit;
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Tests;

public class EntitiesConfigTests
{
    // Helper: create an in-memory capturing logger
    private static (ILogger<EntitiesConfigLoader> logger, List<string> messages) CreateCapturingLogger()
    {
        var messages = new List<string>();
        var factory = LoggerFactory.Create(b =>
            b.AddProvider(new CapturingLoggerProvider(messages)));
        return (factory.CreateLogger<EntitiesConfigLoader>(), messages);
    }

    [Fact]
    public void Load_OneEntityWithHstParams_ParsesCorrectly()
    {
        // Arrange
        var yaml = @"
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: Salon temperatura
    detectors:
      - name: hst
        params:
          window: '250'
          n_trees: '25'
          high_threshold: '0.7'
          low_threshold: '0.3'
          min_consecutive: '3'
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert
        Assert.Single(config.Entities);
        var entity = config.Entities[0];
        Assert.Equal("sensor.salon_temperatura", entity.EntityId);
        Assert.Single(entity.Detectors);
        var det = entity.Detectors[0];
        Assert.Equal("hst", det.Name);
        var hst = HstParams.From(det.Params);
        Assert.Equal(250, hst.Window);
        Assert.Equal(0.7, hst.HighThreshold, precision: 6);
    }

    [Fact]
    public void Load_EntityWithCovariates_ParsesSuccessfullyAndLogsWarning()
    {
        // Arrange
        var yaml = @"
entities:
  - entity_id: sensor.salon_temperatura
    friendly_name: Salon temperatura
    covariates:
      - sensor.outdoor_temperature
    detectors:
      - name: hst
        params: {}
";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: parse succeeded
        Assert.Single(config.Entities);
        // Assert: warning logged mentioning covariates/groups ignored
        Assert.Contains(messages, m =>
            m.Contains("covariates") || m.Contains("groups") || m.Contains("ignored"));
    }

    [Fact]
    public void Load_EntityWithEmptyParams_AppliesDefaults()
    {
        // Arrange
        var yaml = @"
entities:
  - entity_id: sensor.outdoor_temperature
    friendly_name: Zewnatrz temperatura
    detectors:
      - name: hst
        params: {}
";
        var path = WriteTempYaml(yaml);
        var (logger, _) = CreateCapturingLogger();

        // Act
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: defaults applied per D-09/D-11/D-12
        var det = config.Entities[0].Detectors[0];
        var hst = HstParams.From(det.Params);
        Assert.Equal(250, hst.Window);
        Assert.Equal(25, hst.NTrees);
        Assert.Equal(0.7, hst.HighThreshold, precision: 6);
        Assert.Equal(0.3, hst.LowThreshold, precision: 6);
        Assert.Equal(3, hst.MinConsecutive);
        Assert.Equal(10, hst.FrozenWindow);
        Assert.Equal(0.001, hst.FrozenVarianceThreshold, precision: 6);
    }

    [Fact]
    public void Load_EmptyEntities_LogsWarning_DoesNotThrow()
    {
        // Arrange
        var yaml = "entities: []";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act — must NOT throw
        var config = EntitiesConfigLoader.Load(path, logger);

        // Assert: returned config has empty entities (not null)
        Assert.NotNull(config);
        Assert.Empty(config.Entities);

        // Assert: warning logged mentioning empty pipeline / UI
        Assert.Contains(messages, m =>
            m.Contains("no entities") || m.Contains("empty pipeline") || m.Contains("configure via UI"));
    }

    [Fact]
    public void Load_NullEntitiesKey_LogsWarning_DoesNotThrow()
    {
        // Arrange — YAML with no `entities:` key at all (options.json first-boot scenario)
        var yaml = "# empty config";
        var path = WriteTempYaml(yaml);
        var (logger, messages) = CreateCapturingLogger();

        // Act — must NOT throw
        var config = EntitiesConfigLoader.Load(path, logger);

        Assert.NotNull(config);
        Assert.Contains(messages, m => m.Contains("no entities") || m.Contains("empty pipeline"));
    }

    private static string WriteTempYaml(string content)
    {
        var path = System.IO.Path.GetTempFileName() + ".yaml";
        System.IO.File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>Captures log messages into a list for assertion.</summary>
internal class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages;
    public CapturingLoggerProvider(List<string> messages) => _messages = messages;
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
    public void Dispose() { }
}

internal class CapturingLogger : ILogger
{
    private readonly List<string> _messages;
    public CapturingLogger(List<string> messages) => _messages = messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _messages.Add(formatter(state, exception));
    }
}
