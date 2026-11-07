using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Acme.Dns;
using Paduraksa.Application.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Issuance;

/// <summary>The seam DNS providers plug into: one factory per registration, composed into the process's one <see cref="IDnsProviderFactory"/>.</summary>
public sealed class DnsProviderFactoriesTests
{
    /// <summary>A factory that knows the kinds it is given and refuses the others, as <see cref="IDnsProviderFactory"/> says.</summary>
    private sealed class KindFactory(params DnsProviderKind[] known) : IDnsProviderFactory
    {
        public FakeDnsProvider Provider { get; } = new();

        public IDnsProvider Create(DnsProviderKind kind, SecretId credential) =>
            known.Contains(kind) ? Provider : throw new NotSupportedException($"No provider for {kind}.");
    }

    [Fact]
    public void ThePartThatKnowsAKindServesIt()
    {
        KindFactory cloudflare = new(DnsProviderKind.Cloudflare);
        KindFactory desec = new(DnsProviderKind.DeSec);
        CompositeDnsProviderFactory composite = new([cloudflare, desec]);

        Assert.Same(cloudflare.Provider, composite.Create(DnsProviderKind.Cloudflare, SecretId.New()));
        Assert.Same(desec.Provider, composite.Create(DnsProviderKind.DeSec, SecretId.New()));
    }

    [Fact]
    public void AKindNoPartKnowsIsNotSupportedAndNoPartsMeansNothingIs()
    {
        Assert.Throws<NotSupportedException>(() => new CompositeDnsProviderFactory([new KindFactory(DnsProviderKind.Cloudflare)]).Create(DnsProviderKind.DeSec, SecretId.New()));
        Assert.Throws<NotSupportedException>(() => new CompositeDnsProviderFactory([]).Create(DnsProviderKind.Cloudflare, SecretId.New()));
    }

    [Fact]
    public void RegisteredFactoriesAreComposedByTheIssuanceRegistrationWhateverTheOrder()
    {
        KindFactory cloudflare = new(DnsProviderKind.Cloudflare);
        KindFactory desec = new(DnsProviderKind.DeSec);
        ServiceCollection services = new();
        _ = services.AddDnsProviders(_ => cloudflare);
        _ = services.AddPaduraksaIssuance();
        _ = services.AddDnsProviders(_ => desec);

        // Only the factory is resolved here, so none of the issuance services' other dependencies is needed.
        using ServiceProvider provider = services.BuildServiceProvider();
        IDnsProviderFactory composed = provider.GetRequiredService<IDnsProviderFactory>();

        Assert.Same(cloudflare.Provider, composed.Create(DnsProviderKind.Cloudflare, SecretId.New()));
        Assert.Same(desec.Provider, composed.Create(DnsProviderKind.DeSec, SecretId.New()));
    }

    [Fact]
    public void AFactoryOfYourOwnIsLeftAlone()
    {
        KindFactory own = new(DnsProviderKind.Cloudflare);
        ServiceCollection services = new();
        _ = services.AddSingleton<IDnsProviderFactory>(own);
        _ = services.AddPaduraksaIssuance();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<IDnsProviderFactory>());
    }
}
