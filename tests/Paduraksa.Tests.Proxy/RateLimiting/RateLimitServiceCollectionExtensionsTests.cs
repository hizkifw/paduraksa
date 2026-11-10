using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitServiceCollectionExtensionsTests
{
    [Fact]
    public void TheTableSizeComesFromConfiguration()
    {
        using var provider = Build(new() { [RateLimitServiceCollectionExtensions.TableSizeKey] = "4096" });

        Assert.Equal(4_096, provider.GetRequiredService<RateLimitTable>().Capacity);
    }

    [Fact]
    public void TheTableIsTwoHundredFiftySixKiByDefault()
    {
        using var provider = Build([]);

        Assert.Equal(262_144, RateLimitTable.DefaultCapacity);
        Assert.Equal(RateLimitTable.DefaultCapacity, provider.GetRequiredService<RateLimitTable>().Capacity);
    }

    [Theory]
    [InlineData("3000")] // not a power of two
    [InlineData("512")] // below the floor
    [InlineData("33554432")] // above the ceiling
    [InlineData("lots")]
    public void AnImpossibleTableSizeFailsWithTheKeyInTheMessage(string size)
    {
        using var provider = Build(new() { [RateLimitServiceCollectionExtensions.TableSizeKey] = size });

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = provider.GetRequiredService<RateLimitTable>();
        });
        Assert.Contains(RateLimitServiceCollectionExtensions.TableSizeKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEngineIsOneTableOneRegistryAndOneSweeper()
    {
        using var provider = Build([]);

        Assert.Same(provider.GetRequiredService<RateLimitTable>(), provider.GetRequiredService<RateLimitTable>());
        Assert.Same(provider.GetRequiredService<RateLimitPolicySlots>(), provider.GetRequiredService<RateLimitPolicySlots>());
        Assert.Same(provider.GetRequiredService<RateLimitSweeper>(), provider.GetRequiredService<RateLimitSweeper>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<RateLimitSweeper>());
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddPaduraksaRateLimiting();
        return services.BuildServiceProvider();
    }
}
