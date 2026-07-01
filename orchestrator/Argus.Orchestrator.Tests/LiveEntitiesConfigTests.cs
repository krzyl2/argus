using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Tests for LiveEntitiesConfig. Fully offline — no live services required.
/// Covers volatile reference swap semantics, ConfigChanged ordering, and thread safety.
/// </summary>
public class LiveEntitiesConfigTests
{
    // -----------------------------------------------------------------------
    // Get returns initial config
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_AfterConstruction_ReturnsSameInstanceAsCtorArgument()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);

        // Act
        var result = live.Get();

        // Assert
        Assert.Same(initial, result);
    }

    // -----------------------------------------------------------------------
    // Swap + Get
    // -----------------------------------------------------------------------

    [Fact]
    public void Get_AfterSwap_ReturnsNewConfig()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);
        var newConfig = new EntitiesConfig();

        // Act
        live.Swap(newConfig);
        var result = live.Get();

        // Assert
        Assert.Same(newConfig, result);
    }

    [Fact]
    public void Get_AfterSwap_DoesNotReturnOldConfig()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);
        var newConfig = new EntitiesConfig();

        // Act
        live.Swap(newConfig);

        // Assert
        Assert.NotSame(initial, live.Get());
    }

    // -----------------------------------------------------------------------
    // ConfigChanged ordering: event fires AFTER the exchange
    // -----------------------------------------------------------------------

    [Fact]
    public void Swap_FiresConfigChangedAfterExchange()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);
        EntitiesConfig? capturedFromGet = null;

        live.ConfigChanged += (s, e) => capturedFromGet = live.Get();

        var newConfig = new EntitiesConfig();

        // Act
        live.Swap(newConfig);

        // Assert — handler saw the new config, not the old one
        Assert.Same(newConfig, capturedFromGet);
        // After swap, Get() also returns the new config
        Assert.Same(newConfig, live.Get());
    }

    [Fact]
    public void Swap_FiresConfigChangedExactlyOnce()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);
        var fireCount = 0;

        live.ConfigChanged += (s, e) => fireCount++;

        // Act
        live.Swap(new EntitiesConfig());

        // Assert
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Swap_CalledTwice_FiresConfigChangedTwice()
    {
        // Arrange
        var initial = new EntitiesConfig();
        var live = new LiveEntitiesConfig(initial);
        var fireCount = 0;

        live.ConfigChanged += (s, e) => fireCount++;

        // Act
        live.Swap(new EntitiesConfig());
        live.Swap(new EntitiesConfig());

        // Assert
        Assert.Equal(2, fireCount);
    }

    // -----------------------------------------------------------------------
    // Thread safety — concurrent Swap + Get across ~500 iterations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentSwapAndGet_DoesNotThrowAndNeverReturnsNull()
    {
        // Arrange
        var live = new LiveEntitiesConfig(new EntitiesConfig());
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var nullSeen = false;

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                try { live.Swap(new EntitiesConfig()); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        var readerTask = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    var got = live.Get();
                    if (got is null) nullSeen = true;
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        // Act
        await Task.WhenAll(writerTask, readerTask);

        // Assert
        Assert.Empty(exceptions);
        Assert.False(nullSeen, "Get() returned null during concurrent access");
    }
}
