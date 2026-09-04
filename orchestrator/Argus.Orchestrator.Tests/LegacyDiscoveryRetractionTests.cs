using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// D-G. The rule under test is a durability rule, not a code-shape one: the old
/// detector-scoped retained discovery configs must be deleted from the broker EVENTUALLY, and
/// the only thing that may stop the add-on from trying again is proof that the broker already
/// took the deletions.
///
/// Anything weaker leaves an argus_{slug}_{det}_anomaly config retained in the broker forever,
/// which HA turns into a second, orphaned entity fed from the same detector-agnostic
/// argus/{slug}/flag/state topic — the exact duplicate D-G exists to prevent. A broker that is
/// down for the one boot on which the migration happens is an ordinary event, not an exotic one.
/// </summary>
public class LegacyDiscoveryRetractionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _entitiesPath;

    public LegacyDiscoveryRetractionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "argus-retract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _entitiesPath = Path.Combine(_dir, "entities.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly ILogger Silent = NullLogger.Instance;

    /// <summary>The pre-migration config, as the migrator's .pre-v2.bak preserves it.</summary>
    private static readonly IReadOnlyList<EntityConfig> LegacyEntities =
    [
        new() { EntityId = "sensor.load_5m", Detectors = [new DetectorConfig { Name = "hst" }] },
        new() { EntityId = "sensor.tuned_mad", Detectors = [new DetectorConfig { Name = "mad" }] },
    ];

    /// <summary>Simulates a migration that has already run: backup on disk, no marker.</summary>
    private void GivenMigrationHappened()
    {
        File.WriteAllText(_entitiesPath, "schema_version: 2\nentities: []\n");
        File.WriteAllText(_entitiesPath + EntitiesSchemaMigrator.BackupSuffix, "entities: []\n");
    }

    private LegacyDiscoveryRetraction Resolve()
        => LegacyDiscoveryRetraction.Resolve(_entitiesPath, _ => LegacyEntities);

    /// <summary>
    /// THE regression. Boot 1 migrates and fails to reach the broker; boot 2 migrates nothing,
    /// because the file is already schema_version 2. If "is a retraction owed" is answered by
    /// "did we migrate just now", boot 2 answers no and the stale retained configs survive every
    /// subsequent boot. The obligation must outlive the process that incurred it.
    /// </summary>
    [Fact]
    public async Task RetractionSurvivesABootOnWhichTheBrokerWasUnreachable()
    {
        GivenMigrationHappened();

        // Boot 1: the migration ran, the broker did not answer.
        var boot1 = Resolve();
        Assert.True(boot1.IsPending);
        await Assert.ThrowsAsync<TimeoutException>(() => boot1.RunAsync(
            (_, _) => throw new TimeoutException("broker down"), Silent, CancellationToken.None));

        // Boot 2: nothing to migrate any more — and the deletions are still owed.
        var boot2 = Resolve();
        Assert.True(boot2.IsPending);

        var retracted = new List<string>();
        Assert.True(await boot2.RunAsync(
            (entities, _) =>
            {
                retracted.AddRange(entities.Select(e => e.EntityId));
                return Task.CompletedTask;
            },
            Silent, CancellationToken.None));

        Assert.Equal(["sensor.load_5m", "sensor.tuned_mad"], retracted);
    }

    /// <summary>
    /// The other half of the same rule: once the broker HAS taken the deletions, the add-on must
    /// stop republishing them. Deleting an already-deleted retained message is harmless at the
    /// broker, but doing it on every single start is a log line an operator learns to ignore.
    /// </summary>
    [Fact]
    public async Task CompletedRetractionIsNotRepeatedOnTheNextBoot()
    {
        GivenMigrationHappened();

        var runs = 0;
        Assert.True(await Resolve().RunAsync((_, _) => { runs++; return Task.CompletedTask; },
            Silent, CancellationToken.None));

        var next = Resolve();
        Assert.False(next.IsPending);
        Assert.False(await next.RunAsync((_, _) => { runs++; return Task.CompletedTask; },
            Silent, CancellationToken.None));

        Assert.Equal(1, runs);
    }

    /// <summary>
    /// The marker records that the BROKER accepted the deletions, so it must never be written on
    /// a path where it did not. Without this ordering the durable marker would be worse than no
    /// marker at all: it would permanently record a retraction that never reached the broker.
    /// </summary>
    [Fact]
    public async Task MarkerIsNotWrittenWhenTheRetractionFails()
    {
        GivenMigrationHappened();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve().RunAsync(
            (_, _) => throw new InvalidOperationException("not connected"), Silent, CancellationToken.None));

        Assert.False(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));
        Assert.True(Resolve().IsPending);
    }

    /// <summary>
    /// The pre-migration DETECTOR NAMES are what the old unique_ids were built from, and the live
    /// entities.yaml no longer has them — it was rewritten to rmad. So the retraction must read
    /// the backup, never the current file, or it would "retract" ids that were never published.
    /// </summary>
    [Fact]
    public void PendingRetractionReadsTheBackupNotTheMigratedFile()
    {
        GivenMigrationHappened();

        var readFrom = new List<string>();
        var resolved = LegacyDiscoveryRetraction.Resolve(_entitiesPath, path =>
        {
            readFrom.Add(path);
            return LegacyEntities;
        });

        Assert.True(resolved.IsPending);
        Assert.Equal([_entitiesPath + EntitiesSchemaMigrator.BackupSuffix], readFrom);
    }

    /// <summary>
    /// A fresh install has never published a detector-scoped id, so there is nothing to delete and
    /// no marker to write — the add-on must not announce a retraction it did not need.
    /// </summary>
    [Fact]
    public void NoBackupMeansNothingWasEverMigratedAndNothingIsRetracted()
    {
        File.WriteAllText(_entitiesPath, "schema_version: 2\nentities: []\n");

        Assert.False(Resolve().IsPending);
    }
}
