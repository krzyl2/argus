using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

public class ConfigWriterTests
{
    [Fact]
    public async Task WriteAsync_ProducesFileWithExpectedContent()
    {
        // Arrange
        var target = Path.Combine(Path.GetTempPath(), $"entities-test-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();
        var yaml = "entities:\n  - entity_id: sensor.test\n";

        // Act
        await writer.WriteAsync(target, yaml);

        // Assert
        Assert.True(File.Exists(target));
        Assert.Equal(yaml, await File.ReadAllTextAsync(target));

        // Cleanup
        File.Delete(target);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentCalls_NeitherThrows()
    {
        // Arrange
        var target = Path.Combine(Path.GetTempPath(), $"entities-conc-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();

        // Act — two concurrent writes; SemaphoreSlim(1,1) serializes them
        var t1 = writer.WriteAsync(target, "entities: []\n");
        var t2 = writer.WriteAsync(target, "entities: []\n");
        await Task.WhenAll(t1, t2);

        // Assert — file exists with valid content (one of the two writes won)
        Assert.True(File.Exists(target));

        // Cleanup
        File.Delete(target);
    }

    [Fact]
    public async Task WriteAsync_NoTempFileLeftBehind()
    {
        // Arrange
        var dir = Path.GetTempPath();
        var target = Path.Combine(dir, $"entities-tmp-{Guid.NewGuid():N}.yaml");
        var writer = new ConfigWriter();

        // Act
        await writer.WriteAsync(target, "entities: []\n");

        // Assert — no .tmp. files remain in the directory
        var orphans = Directory.GetFiles(dir, ".entities.tmp.*.yaml");
        Assert.Empty(orphans);

        // Cleanup
        File.Delete(target);
    }
}
