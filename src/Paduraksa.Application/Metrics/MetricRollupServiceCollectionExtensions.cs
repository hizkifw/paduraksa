using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Data;

namespace Paduraksa.Application.Metrics;

public static class MetricRollupServiceCollectionExtensions
{
    /// <summary>Registers the rollup store over the config database's context factory (which the services must already hold).</summary>
    public static IServiceCollection AddPaduraksaMetricRollups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(static provider => new MetricRollupStore(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
    }
}
