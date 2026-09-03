using Argus.Orchestrator.Ha;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// WS4/F10: ordering contract of the per-connection snapshot passes
/// (<see cref="NetDaemonHaEventSource.RunSnapshotPassesAsync"/>). Driven through its delegates,
/// so no live socket and no real 60 s wait are involved.
/// </summary>
public class NetDaemonHaEventSourceTests
{
    private static HaStateDto Dto(string entityId, string? state) =>
        new(entityId, state, DateTime.UtcNow, null, null);

    /// <summary>Records the call sequence so ordering can be asserted as one list, not as flags.</summary>
    private sealed class CallLog
    {
        public List<string> Calls { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
    }

    private static async Task<CallLog> RunAsync(
        bool isFirstConnection,
        int settleSeconds,
        params IReadOnlyList<HaStateDto>[] snapshots)
    {
        var log = new CallLog();
        var pass = 0;

        await NetDaemonHaEventSource.RunSnapshotPassesAsync(
            isFirstConnection: isFirstConnection,
            settleSeconds: settleSeconds,
            getStates: _ =>
            {
                log.Calls.Add("get_states");
                var result = snapshots[Math.Min(pass, snapshots.Length - 1)];
                pass++;
                return Task.FromResult(result);
            },
            onSnapshot: (states, passName, _) =>
            {
                log.Calls.Add($"snapshot:{passName}:{states.Count}");
                return Task.CompletedTask;
            },
            afterSnapshots: (states, _) =>
            {
                log.Calls.Add($"after:{states.Count}");
                return Task.CompletedTask;
            },
            subscribe: _ =>
            {
                log.Calls.Add("subscribe");
                return Task.CompletedTask;
            },
            delay: (d, _) =>
            {
                log.Calls.Add("delay");
                log.Delays.Add(d);
                return Task.CompletedTask;
            },
            ct: CancellationToken.None);

        return log;
    }

    [Fact]
    public async Task FirstConnection_RunsSettleSnapshot_BeforeSubscribe()
    {
        // WHY: HaWebSocketClient has NO message router — a get_states issued after
        // subscribe_events would read state_changed frames as its own reply. So the settle pass is
        // only safe strictly before the subscription, and this test is the thing that keeps it there.
        // The pass exists at all because integrations still loading at add-on boot report
        // `unknown`, fail the numeric filter, and (pre-WS4) stayed invisible until a reconnect.
        var boot = new[] { Dto("sensor.a", "1.0"), Dto("number.b", "unknown") };
        var settled = new[] { Dto("sensor.a", "1.1"), Dto("number.b", "5.0") };

        var log = await RunAsync(isFirstConnection: true, settleSeconds: 60, boot, settled);

        Assert.Equal(
            [
                "get_states",
                "snapshot:initial:2",
                "delay",
                "get_states",
                "snapshot:settle:2",
                "after:2",
                "subscribe",
            ],
            log.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(log.Delays));
    }

    [Fact]
    public async Task SettleSecondsZero_IssuesExactlyOneGetStates()
    {
        // WHY: the knob has to be a real off switch. 0 must reproduce pre-WS4 behaviour exactly —
        // one snapshot, no added delay before the subscription opens (the delay postpones the first
        // scoring of every entity by that many seconds).
        var log = await RunAsync(isFirstConnection: true, settleSeconds: 0, [Dto("sensor.a", "1.0")]);

        Assert.Equal(["get_states", "snapshot:initial:1", "after:1", "subscribe"], log.Calls);
        Assert.Empty(log.Delays);
    }

    [Fact]
    public async Task Reconnect_NeverRunsSettlePass()
    {
        // WHY: the settle pass pays a delay to work around BOOT-time integration loading. On a
        // reconnect HA is already up, so the same delay would only postpone D-07 resnapshot feeding
        // and the binary_sensor suppression window that depends on it.
        var log = await RunAsync(isFirstConnection: false, settleSeconds: 60, [Dto("sensor.a", "1.0")]);

        Assert.Equal(["get_states", "snapshot:reconnect:1", "after:1", "subscribe"], log.Calls);
        Assert.Empty(log.Delays);
    }
}
