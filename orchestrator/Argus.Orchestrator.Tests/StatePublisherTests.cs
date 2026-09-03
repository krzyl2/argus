using Argus.Orchestrator.Mqtt;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for StatePublisher's wire-level contract: which topic carries which payload, and
/// which of them is retained.
/// </summary>
public class StatePublisherTests
{
    private sealed record Published(string Topic, string Payload, bool Retain);

    private sealed class RecordingSink : IMqttPublishSink
    {
        public List<Published> Messages { get; } = new();

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
        {
            Messages.Add(new Published(topic, payload, retain));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PublishFlagAsync_UsesRetainTrue_AndScoreUsesRetainFalse()
    {
        // The two topics need OPPOSITE retain flags, and the reason is the change-only publish
        // rule introduced with the alert layer:
        //  - flag: published only on a transition, so a non-retained topic leaves HA showing
        //    `unknown` after every broker or HA restart until the next real transition — which
        //    on a healthy sensor may never come. A stale retained ON is covered by the bridge
        //    LWT plus the availability list in retained discovery.
        //  - score: republished on every verdict anyway, so retaining it would only pin a stale
        //    number and change nothing an operator can see.
        var sink = new RecordingSink();
        var publisher = new StatePublisher();
        publisher.SetConnection(sink);

        await publisher.PublishFlagAsync("sensor.living_room_temp", on: true, CancellationToken.None);
        await publisher.PublishScoreAsync("sensor.living_room_temp", 0.42, CancellationToken.None);

        var flag = Assert.Single(sink.Messages, m => m.Topic.EndsWith("/flag/state"));
        Assert.Equal("argus/sensor_living_room_temp/flag/state", flag.Topic);
        Assert.Equal("ON", flag.Payload);
        Assert.True(flag.Retain, "The flag topic must be retained (change-only publishing)");

        var score = Assert.Single(sink.Messages, m => m.Topic.EndsWith("/score/state"));
        Assert.Equal("argus/sensor_living_room_temp/score/state", score.Topic);
        Assert.False(score.Retain, "The score topic must stay non-retained");
    }

    [Fact]
    public async Task PublishScoreAsync_PayloadIsInvariantCultureG_Format()
    {
        // A11: the score payload must be byte-identical to what shipped before this change, so
        // no HA entity churns and no dashboard template breaks. "G" + InvariantCulture is the
        // whole of that contract — a comma decimal separator would land in HA as a broken state.
        var sink = new RecordingSink();
        var publisher = new StatePublisher();
        publisher.SetConnection(sink);

        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
            await publisher.PublishScoreAsync("sensor.x", 0.4321, CancellationToken.None);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }

        Assert.Equal("0.4321", Assert.Single(sink.Messages).Payload);
    }
}
