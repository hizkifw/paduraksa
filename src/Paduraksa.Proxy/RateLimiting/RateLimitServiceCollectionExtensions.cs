using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Registers the rate-limit engine's process-lifetime state: the bucket table, the policy slot registry and the sweeper
/// (PLAN §6.3). Nothing calls <see cref="RateLimitTable.TryAcquire"/> yet; the gate's rate-limit policy will.
/// </summary>
public static class RateLimitServiceCollectionExtensions
{
    /// <summary>The table's entry count, a power of two from <see cref="RateLimitTable.MinimumCapacity"/> to
    /// <see cref="RateLimitTable.MaximumCapacity"/>; absent means <see cref="RateLimitTable.DefaultCapacity"/>.</summary>
    public const string TableSizeKey = "Paduraksa:RateLimiting:TableSize";

    public static IServiceCollection AddPaduraksaRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static provider => new RateLimitTable(ConfiguredTableSize(provider.GetService<IConfiguration>())));
        services.TryAddSingleton(static provider => new RateLimitPolicySlots(provider.GetRequiredService<RateLimitTable>()));
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
