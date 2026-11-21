using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Data;
using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Application.Honeypot;

public static class HoneypotServiceCollectionExtensions
{
    /// <summary>
    /// Registers the honeypot's store and read service over the config database's context factory (which the services must already hold), the
    /// recorder the trap's sink is (which the process already created) and the counters it shares with the trap.
    /// </summary>
    public static IServiceCollection AddPaduraksaHoneypotRecording(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddSingleton(static provider => new HoneypotStore(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), provider.GetRequiredService<HoneypotRecorder>()))
            .AddSingleton(static provider => new HoneypotService(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), provider.GetRequiredService<HoneypotStats>(), provider.GetRequiredService<TimeProvider>()));
    }
}
