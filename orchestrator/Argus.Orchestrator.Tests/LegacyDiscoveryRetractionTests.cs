using Argus.Orchestrator.Config;
using Argus.Orchestrator.Mqtt;
using Argus.Orchestrator.Workers;
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
                return Task.FromResult(true);
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
        Assert.True(await Resolve().RunAsync((_, _) => { runs++; return Task.FromResult(true); },
            Silent, CancellationToken.None));

        var next = Resolve();
        Assert.False(next.IsPending);
        Assert.False(await next.RunAsync((_, _) => { runs++; return Task.FromResult(true); },
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
    /// THE half of the same rule that a throwing fake cannot reach, and the one the field
    /// actually hits: the production sink DROPS instead of throwing. MqttConnection.PublishAsync
    /// logs "MQTT not connected — dropped publish" and returns normally, so a retraction that
    /// judges itself by "the loop finished" reports success having deleted nothing — and the
    /// marker it then writes retires the obligation FOREVER, which is worse than never having
    /// tried. Success has to mean delivery.
    /// </summary>
    [Fact]
    public async Task MarkerIsNotWrittenWhenTheBrokerSilentlyDroppedTheDeletions()
    {
        GivenMigrationHappened();

        // No throw anywhere: the deletions were simply dropped on the floor, exactly as an
        // unreachable broker looks from inside PublishAsync.
        Assert.False(await Resolve().RunAsync(
            (_, _) => Task.FromResult(false), Silent, CancellationToken.None));

        Assert.False(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));

        // Still owed on the next boot, and completed once the broker is back.
        var next = Resolve();
        Assert.True(next.IsPending);
        Assert.True(await next.RunAsync((_, _) => Task.FromResult(true), Silent, CancellationToken.None));
        Assert.True(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));
    }

    /// <summary>
    /// The same rule wired to the REAL sink instead of a fake, because the fakes above are only
    /// as honest as the delegate they stand in for. An MqttConnection that never connected is
    /// what a down broker gives the retraction on boot: every publish is dropped, so the whole
    /// retraction must come back false and leave no marker behind.
    ///
    /// The production path is asserted through DiscoveryPublisher.RetractLegacyDetectorScopedAsync
    /// on a live MqttConnection — the exact delegate MqttPublisherWorker passes.
    /// </summary>
    [Fact]
    public async Task RealMqttConnectionWithNoBroker_LeavesTheRetractionOwed()
    {
        GivenMigrationHappened();

        await using var mqtt = new MqttConnection(
            new MqttConnectionTests.FakeCredentialSource(new ConnectionSettings
            {
                MqttHost = "localhost",
                MqttPort = 1883,
                MqttUser = "u",
                MqttPassword = "p",
            }),
            NullLogger<MqttConnection>.Instance)
        {
            PublishConnectionWait = TimeSpan.FromMilliseconds(20),
        };

        var completed = await Resolve().RunAsync(
            (entities, ct) => DiscoveryPublisher.RetractLegacyDetectorScopedAsync(mqtt, entities, ct),
            Silent, CancellationToken.None);

        Assert.False(completed);
        Assert.False(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));
        Assert.True(Resolve().IsPending);
    }

    /// <summary>
    /// A retraction that fails must not become a boot loop. The obligation is durable now, so a
    /// deterministic throw would be re-thrown on every start; with StopHost that is start-crash-
    /// start with PublishAllAsync never running — no discovery in HA AT ALL. A missing retraction
    /// costs one duplicate entity per sensor; a boot loop costs every entity. So the worker logs
    /// it and carries on, and — no marker having been written — retries on the next start.
    /// </summary>
    [Fact]
    public async Task AFailedRetractionDoesNotStopTheBoot()
    {
        GivenMigrationHappened();

        var completed = await MqttPublisherWorker.RunLegacyRetractionAsync(
            Resolve(),
            (_, _) => throw new InvalidOperationException("broker refused the connection"),
            Silent, CancellationToken.None);

        Assert.False(completed);
        Assert.False(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));
        Assert.True(Resolve().IsPending);
    }

    /// <summary>
    /// Host shutdown is not a failure to swallow: cancellation must still propagate so the stop
    /// stays clean (and so a cancelled publish is never mistaken for a completed one).
    /// </summary>
    [Fact]
    public async Task AShutdownDuringTheRetractionStillCancels()
    {
        GivenMigrationHappened();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MqttPublisherWorker.RunLegacyRetractionAsync(
                Resolve(),
                (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); },
                Silent, cts.Token));

        Assert.False(File.Exists(_entitiesPath + LegacyDiscoveryRetraction.MarkerSuffix));
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
