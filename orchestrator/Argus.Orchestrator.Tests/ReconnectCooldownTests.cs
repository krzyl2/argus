using Argus.Orchestrator.Ha;
using System;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for ReconnectCooldown 60-second binary_sensor suppression window (D-07).
/// All tests use explicit timestamps — no Thread.Sleep.
/// </summary>
public class ReconnectCooldownTests
{
    [Fact]
    public void IsSuppressed_ImmediatelyAfterMarkReconnect_ReturnsTrue()
    {
        var cooldown = new ReconnectCooldown();
        var now = DateTimeOffset.UtcNow;

        cooldown.MarkReconnect(now);

        Assert.True(cooldown.IsSuppressed(now));
    }

    [Fact]
    public void IsSuppressed_At59SecondsAfterMarkReconnect_ReturnsTrue()
    {
        var cooldown = new ReconnectCooldown();
        var now = DateTimeOffset.UtcNow;

        cooldown.MarkReconnect(now);

        Assert.True(cooldown.IsSuppressed(now.AddSeconds(59)));
    }

    [Fact]
    public void IsSuppressed_At61SecondsAfterMarkReconnect_ReturnsFalse()
    {
        var cooldown = new ReconnectCooldown();
        var now = DateTimeOffset.UtcNow;

        cooldown.MarkReconnect(now);

        Assert.False(cooldown.IsSuppressed(now.AddSeconds(61)));
    }

    [Fact]
    public void IsSuppressed_WithNoReconnectRecorded_ReturnsFalse()
    {
        var cooldown = new ReconnectCooldown();

        Assert.False(cooldown.IsSuppressed(DateTimeOffset.UtcNow));
    }
}
