using Argus.Orchestrator.Batch;
using Argus.Orchestrator.Config;

namespace Argus.Orchestrator.Ha;

/// <summary>
/// The no-InfluxDB branch of the composition root, extracted so it is directly testable
/// (F11): "IInfluxDataSource resolves to something when influx_url is empty" is the whole
/// point of WS5, and asserting it against a hand-copied ServiceCollection in a test would
/// assert a copy of Program.cs rather than Program.cs itself.
/// </summary>
internal static class HaRecorderHistoryRegistration
{
    /// <summary>
    /// Registers <see cref="HaRecorderHistorySource"/> as the process's
    /// <see cref="IInfluxDataSource"/>. Explicit factory (not AddSingleton&lt;T&gt;) because the
    /// class has optional test-seam constructor parameters that DI must not try to resolve.
    /// </summary>
    public static IServiceCollection AddHaRecorderHistorySource(this IServiceCollection services)
    {
        services.AddSingleton<HaRecorderHistorySource>(sp => new HaRecorderHistorySource(
            sp.GetRequiredService<ConnectionSettings>(),
            sp.GetRequiredService<ILogger<HaRecorderHistorySource>>()));
        services.AddSingleton<IInfluxDataSource>(sp => sp.GetRequiredService<HaRecorderHistorySource>());
        return services;
    }
}
