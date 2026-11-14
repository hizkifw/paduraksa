using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme.Dns;
using Paduraksa.Application.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Dns;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

public sealed class DnsProviderRegistrationTests
{
    private static ServiceCollection Services(ISecretHttpHandlerFactory handlers)
    {
        ServiceCollection services = new();
        services.AddSingleton(handlers);
        return services;
    }

    [Fact]
    public void ProvidersAndTheAuthoritativeCheckAreRegisteredAndAnythingPresentIsKept()
    {
        FakeCloudflare cloudflare = new();
        var services = Services(new FakeSecretHandlers(cloudflare.Handle));

        using var provider = services.AddPaduraksaDnsProviders().BuildServiceProvider();

        Assert.IsType<DnsProviderFactory>(provider.GetRequiredService<IDnsProviderFactory>());
        Assert.IsType<AuthoritativeDnsPropagationCheck>(provider.GetRequiredService<IDnsPropagationCheck>());

        FakePropagationCheck mine = new();
        var custom = Services(new FakeSecretHandlers(cloudflare.Handle));
        custom.AddSingleton<IDnsPropagationCheck>(mine);
        using var kept = custom.AddPaduraksaDnsProviders().BuildServiceProvider();
        Assert.Same(mine, kept.GetRequiredService<IDnsPropagationCheck>());
    }

    [Fact]
    public void TheCheckIsRegisteredEvenWhenOffSoItCanBeSwitchedOnWithNoRestart()
    {
        // Off is the issuance options' DnsPropagationCheck, a server setting; registering the check sends no DNS query.
        using var provider = Services(new FakeSecretHandlers(new FakeCloudflare().Handle)).AddPaduraksaDnsProviders(new DnsPropagationSettings { Enabled = false }).BuildServiceProvider();

        Assert.NotNull(provider.GetService<IDnsPropagationCheck>());
        Assert.NotNull(provider.GetService<IDnsProviderFactory>());
    }

    [Fact]
    public void BothKindsAreBuiltAndAnUnknownKindIsRefused()
    {
        using var provider = Services(new FakeSecretHandlers(new FakeCloudflare().Handle)).AddPaduraksaDnsProviders().BuildServiceProvider();
        var factory = provider.GetRequiredService<IDnsProviderFactory>();

        Assert.NotNull(factory.Create(DnsProviderKind.Cloudflare, SecretId.New()));
        Assert.NotNull(factory.Create(DnsProviderKind.DeSec, SecretId.New()));
        Assert.Throws<NotSupportedException>(() => factory.Create((DnsProviderKind)99, SecretId.New()));
    }

    [Fact]
    public async Task TestCredentialsRunsTheRealCloudflareProviderOverAFakeHandler()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        FakeCloudflare cloudflare = new();
        cloudflare.AddZone("example.com");
        FakeSecretHandlers handlers = new(cloudflare.Handle);
        using var provider = Services(handlers).AddPaduraksaDnsProviders().BuildServiceProvider();
        DnsProviderService service = new(h.Config.Db.Factory, h.Config.Service, h.Secrets, provider.GetRequiredService<IDnsProviderFactory>(), h.Time, NullLogger<DnsProviderService>.Instance, h.Permissions);
        var row = await h.CreateDnsProviderAsync();

        Assert.Equal(new Paduraksa.Contracts.Issuance.DnsProviderTestResult(true, true), await service.TestAsync(row.Id, Paduraksa.Application.Commands.CommandOrigin.Cli, Ct));

        cloudflare.Intercept = _ => FakeCloudflare.Error(System.Net.HttpStatusCode.Forbidden, 9109, "Unauthorized to access requested resource");
        var refused = await service.TestAsync(row.Id, Paduraksa.Application.Commands.CommandOrigin.Cli, Ct);
        Assert.False(refused.Valid);
        Assert.Contains("9109", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretHandlers.Token, refused.Message, StringComparison.Ordinal);

        // A provider outage is a failed test with the reason, not an exception.
        cloudflare.Intercept = _ => FakeSecretHandlers.Empty(System.Net.HttpStatusCode.BadGateway);
        var outage = await service.TestAsync(row.Id, Paduraksa.Application.Commands.CommandOrigin.Cli, Ct);
        Assert.False(outage.Valid);
        Assert.Contains("502", outage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsBecomeTheChecksOptionsAndBadOnesAreRefused()
    {
        var options = new DnsPropagationSettings
        {
            Timeout = TimeSpan.FromMinutes(2),
            Resolvers = ["10.0.0.2", "10.0.0.3:5353", "[2001:db8::1]:53"],
        }.ToOptions();

        Assert.Equal(TimeSpan.FromMinutes(2), options.Timeout);
        Assert.Equal(["10.0.0.2:53", "10.0.0.3:5353", "[2001:db8::1]:53"], options.Resolvers.Select(r => r.ToString()));
        Assert.Throws<ArgumentException>(() => new DnsPropagationSettings { Resolvers = ["not an address"] }.ToOptions());
        Assert.Throws<ArgumentException>(() => new DnsPropagationSettings { Timeout = TimeSpan.Zero }.ToOptions());
        Assert.Throws<ArgumentException>(() => new DnsPropagationSettings { InitialInterval = TimeSpan.FromSeconds(30), MaxInterval = TimeSpan.FromSeconds(5) }.ToOptions());
    }

    [Fact]
    public void TheSettingsReachTheRegisteredCheck()
    {
        using var provider = Services(new FakeSecretHandlers(new FakeCloudflare().Handle))
            .AddPaduraksaDnsProviders(new DnsPropagationSettings { Timeout = TimeSpan.FromSeconds(9), Resolvers = ["10.1.1.1"] })
            .BuildServiceProvider();

        var options = provider.GetRequiredService<DnsPropagationOptions>();

        Assert.Equal(TimeSpan.FromSeconds(9), options.Timeout);
        Assert.Equal("10.1.1.1:53", Assert.Single(options.Resolvers).ToString());
    }
}
