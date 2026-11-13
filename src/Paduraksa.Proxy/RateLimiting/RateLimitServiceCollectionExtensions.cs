using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Registers the rate-limit engine's process-lifetime state: the bucket table, the policy slot registry, the counters and
/// the sweeper (docs/design/config.md "State that survives a publish"). The gate's rate-limit policies take tokens from the same table the compiler assigned slots
/// in.
/// </summary>
public static class RateLimitServiceCollectionExtensions
{
    /// <summary>The table's entry count, a power of two from <see cref="RateLimitTable.MinimumCapacity"/> to
    /// <see cref="RateLimitTable.MaximumCapacity"/>; absent means <see cref="RateLimitTable.DefaultCapacity"/>.</summary>
    public const string TableSizeKey = "Paduraksa:RateLimiting:TableSize";

    /// <summary>The process's runtime, built from <paramref name="configuration"/> (the configured table size). The core
    /// host builds one and hands it to the publisher, the compiler and the public app, so all three share it.</summary>
    public static RateLimitRuntime CreateRuntime(IConfiguration? configuration = null) =>
        new(new RateLimitTable(ConfiguredTableSize(configuration)));

    /// <summary>
    /// Registers the runtime (the one given, or a fresh one from the container's configuration), its table, slots and
    /// counters as singletons, and the sweeper as a hosted service.
    /// </summary>
    public static IServiceCollection AddPaduraksaRateLimiting(this IServiceCollection services, RateLimitRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => runtime ?? CreateRuntime(provider.GetService<IConfiguration>()));
        services.TryAddSingleton(static provider => provider.GetRequiredService<RateLimitRuntime>().Table);
        services.TryAddSingleton(static provider => provider.GetRequiredService<RateLimitRuntime>().Slots);
        services.TryAddSingleton(static provider => provider.GetRequiredService<RateLimitRuntime>().Stats);
        services.TryAddSingleton<RateLimitSweeper>();
        services.AddHostedService(static provider => provider.GetRequiredService<RateLimitSweeper>());
        return services;
    }

    private static int ConfiguredTableSize(IConfiguration? configuration)
    {
        var text = configuration?[TableSizeKey];
        if (string.IsNullOrEmpty(text))
        {
            return RateLimitTable.DefaultCapacity;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            throw new InvalidOperationException($"{TableSizeKey} must be a whole number, was '{text}'.");
        }

        if (size < RateLimitTable.MinimumCapacity || size > RateLimitTable.MaximumCapacity || (size & (size - 1)) != 0)
        {
            throw new InvalidOperationException(
                $"{TableSizeKey} must be a power of two between {RateLimitTable.MinimumCapacity} and {RateLimitTable.MaximumCapacity}, was {size}.");
        }

        return size;
    }
}
