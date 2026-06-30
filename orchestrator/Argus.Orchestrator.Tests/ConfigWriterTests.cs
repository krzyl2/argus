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

    [Fact]
    public async Task WriteAsync_FailedMove_LeavesNoOrphanTempFile()
    {
        // Arrange — target path has a null byte in the filename, which causes File.Move to
        // throw ArgumentException AFTER WriteAllTextAsync has already created the temp file.
        // This exercises the WR-01 cleanup path: temp file must be deleted in finally.
        var dir = Path.GetTempPath();
        var invalidTarget = Path.Combine(dir, "entities-invalid\0.yaml"); // \0 triggers ArgumentException in Move
        var writer = new ConfigWriter();

        // Act — must throw (invalid target path)
        await Assert.ThrowsAnyAsync<Exception>(() =>
            writer.WriteAsync(invalidTarget, "entities: []\n"));

        // Assert — no .entities.tmp.*.yaml orphan remains in the temp directory
        var orphans = Directory.GetFiles(dir, ".entities.tmp.*.yaml");
        Assert.Empty(orphans);
    }
}
