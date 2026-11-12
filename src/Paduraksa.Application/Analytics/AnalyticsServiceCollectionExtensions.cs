using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Data;
using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Application.Analytics;

public static class AnalyticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the analytics rollup store over the config database's context factory (which the services must already hold) and the
    /// backlog the drain closes buckets into (which the process already created and the services must hold).
    /// </summary>
    public static IServiceCollection AddPaduraksaAnalyticsRollups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(static provider => new AnalyticsRollupStore(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), provider.GetRequiredService<AnalyticsBacklog>()));
    }
}
