using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Config;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Certificates;

public sealed class CertificateInventoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(1);

    private readonly List<X509Certificate2> _owned = [];
    private readonly Source _source = new();
    private readonly BuiltIns _builtIns = new();
    private readonly Clock _clock = new(Now);

    public void Dispose()
    {
        foreach (X509Certificate2 certificate in _owned)
        {
            certificate.Dispose();
        }
    }

    [Theory]
    [InlineData("a.example.com", "a.example.com", "Exact")]
    [InlineData("www.example.com", "*.example.com", "Wildcard")]
    [InlineData("example.com", "*.example.com", "None")] // a wildcard doesn't cover its apex
    [InlineData("a.b.example.com", "*.example.com", "None")] // one label only
    [InlineData("*.example.com", "*.example.com", "Exact")] // a wildcard host by the same wildcard
    [InlineData("*.example.com", "a.example.com", "None")]
    [InlineData("*.b.example.com", "*.example.com", "None")]
    [InlineData("other.com", "*.example.com", "None")]
    [InlineData("xexample.com", "*.example.com", "None")]
    public void CoveringFollowsTlsWildcardRules(string host, string certificateName, string expected) =>
        Assert.Equal(expected, CertificateSelection.Match([certificateName], host).ToString());

    [Fact]
    public void AnExactNameBeatsAWildcardEvenWhenTheWildcardLastsLonger()
    {
        InventoryCertificate wildcard = Entry("wild", ["*.example.com"], notAfter: Now.AddDays(80));
        InventoryCertificate exact = Entry("exact", ["www.example.com"], notAfter: Now.AddDays(10));

        Assert.Same(exact, CertificateSelection.Best([wildcard, exact], "www.example.com"));
        Assert.Same(wildcard, CertificateSelection.Best([wildcard, exact], "api.example.com"));
        Assert.Null(CertificateSelection.Best([wildcard, exact], "example.com"));
    }

    [Fact]
    public void AmongEqualMatchesTheLatestNotAfterWinsThenTheLatestNotBeforeThenTheSmallestId()
    {
        InventoryCertificate old = Entry("old", ["a.example.com"], notAfter: Now.AddDays(10));
        InventoryCertificate renewed = Entry("renewed", ["a.example.com"], notAfter: Now.AddDays(70));
        InventoryCertificate laterStart = Entry("laterStart", ["a.example.com"], notAfter: Now.AddDays(70), notBefore: Now);

        Assert.Same(renewed, CertificateSelection.Best([old, renewed], "a.example.com"));
        Assert.Same(laterStart, CertificateSelection.Best([renewed, laterStart, old], "a.example.com"));

        InventoryCertificate twin = renewed with { Id = Id<CertificateId>(1) };
        InventoryCertificate other = renewed with { Id = Id<CertificateId>(2) };
        Assert.Equal(Id<CertificateId>(1), CertificateSelection.Best([other, twin], "a.example.com")!.Id);
    }

    [Fact]
    public void ServerNamesAreTheNormalisedDnsNamesOnly() =>
        Assert.Equal(["a.example.com", "*.b.example.com"], CertificateSelection.ServerNames(["A.Example.com.", "192.0.2.7", "[2001:db8::1]", "*.B.example.com", "a.example.com"]));

    [Fact]
    public async Task AutoPicksPerNameSoAMultiNameHostCanUseSeveralCertificates()
    {
        CertificateId exact = Add("exact", ["example.com", "www.example.com"], Now.AddDays(5));
        CertificateId wildcard = Add("wildcard", ["*.example.com"], Now.AddDays(60));
        ConfigDocument document = Document(Host("multi", ["example.com", "www.example.com", "api.example.com", "nothing.test"]));

        ConfigCompilation compilation = await CompileAsync(document);

        Assert.Same(_source.ContextOf(exact), Served(compilation, "example.com"));
        Assert.Same(_source.ContextOf(exact), Served(compilation, "www.example.com")); // exact beats the wildcard that lasts longer
        Assert.Same(_source.ContextOf(wildcard), Served(compilation, "api.example.com"));
        Assert.Null(Served(compilation, "nothing.test")); // no certificate of its own, and no default

        ConfigWarning warning = Assert.Single(compilation.Warnings);
        Assert.Equal((ConfigWarningCodes.CertificateAutoNoCoverage, "/hosts/0"), (warning.Code, warning.Path));
        Assert.Contains("nothing.test", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", warning.Message.Replace("nothing.test", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredAndNotYetValidCertificatesAreNeverChosenAndNeverLoaded()
    {
        CertificateId expired = Add("expired", ["a.example.com"], Now.AddDays(-1), notBefore: Now.AddDays(-90));
        CertificateId future = Add("future", ["a.example.com"], Now.AddDays(100), notBefore: Now.AddDays(10));
        CertificateId valid = Add("valid", ["a.example.com"], Now.AddDays(3));

        ConfigCompilation compilation = await CompileAsync(Document(Host("a", ["a.example.com"])));

        Assert.Same(_source.ContextOf(valid), Served(compilation, "a.example.com"));
        Assert.Equal([valid], _source.Loaded);
        Assert.Empty(compilation.Warnings);

        _clock.Now = Now.AddDays(5); // the valid one runs out too
        compilation = await CompileAsync(Document(Host("a", ["a.example.com"])));
        Assert.Equal((null, ConfigWarningCodes.CertificateAutoNoCoverage), (Served(compilation, "a.example.com"), Assert.Single(compilation.Warnings).Code));
        Assert.DoesNotContain(expired, _source.Loaded);
        Assert.DoesNotContain(future, _source.Loaded);
    }

    [Fact]
    public async Task ACertificateTheVaultCantLoadIsLeftOutAndTheNextBestTakesOver()
    {
        CertificateId best = Add("best", ["a.example.com"], Now.AddDays(60));
        CertificateId next = Add("next", ["a.example.com"], Now.AddDays(30));
        _source.Missing.Add(best); // the row is there, the key is gone

        ConfigCompilation compilation = await CompileAsync(Document(Host("a", ["a.example.com"])));

        Assert.True(compilation.Succeeded);
        Assert.Same(_source.ContextOf(next), Served(compilation, "a.example.com"));
        Assert.Empty(compilation.Warnings);
    }

    [Fact]
    public async Task ASourceThatThrowsNeverFailsTheCompile()
    {
        CertificateId broken = Add("broken", ["a.example.com"], Now.AddDays(60));
        _source.Throwing.Add(broken);
        CertificateBinding specific = new() { Mode = CertificateBindingMode.Specific, CertificateId = broken };

        ConfigCompilation compilation = await CompileAsync(Document(Host("a", ["a.example.com"]), Host("b", ["b.example.com"], specific)));

        Assert.True(compilation.Succeeded);
        Assert.Null(Served(compilation, "a.example.com"));
        Assert.Contains(compilation.Warnings, w => w.Code == ConfigWarningCodes.CertificateAutoNoCoverage && w.Path == "/hosts/0");
        Assert.Contains(compilation.Warnings, w => w.Code == ConfigWarningCodes.CertificateSpecificUnusable && w.Path == "/hosts/1/certificate");
    }

    [Fact]
    public async Task ASpecificBindingToAnExpiredOrUnknownCertificateWarnsInsteadOfFailing()
    {
        CertificateId expired = Add("expired", ["a.example.com"], Now.AddDays(-1), notBefore: Now.AddDays(-90));
        CertificateBinding toExpired = new() { Mode = CertificateBindingMode.Specific, CertificateId = expired };
        CertificateBinding toUnknown = new() { Mode = CertificateBindingMode.Specific, CertificateId = Id<CertificateId>(99) };

        ConfigCompilation compilation = await CompileAsync(Document(
            Host("a", ["a.example.com"], toExpired), Host("c", ["c.example.com"], toUnknown)));

        Assert.True(compilation.Succeeded);
        Assert.Equal(["/hosts/0/certificate", "/hosts/1/certificate"], compilation.Warnings.Select(w => w.Path));
        Assert.All(compilation.Warnings, w => Assert.Equal(ConfigWarningCodes.CertificateSpecificUnusable, w.Code));
        Assert.Contains("expired", compilation.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("no such certificate", compilation.Warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpecificCertificateThatDoesntCoverTheNamesWarnsButIsServed()
    {
        CertificateId other = Add("other", ["other.test"], Now.AddDays(60));
        CertificateBinding specific = new() { Mode = CertificateBindingMode.Specific, CertificateId = other };

        ConfigCompilation compilation = await CompileAsync(Document(Host("a", ["a.example.com"], specific)));

        Assert.Same(_source.ContextOf(other), Served(compilation, "a.example.com"));
        Assert.Equal(ConfigWarningCodes.CertificateSpecificNoCoverage, Assert.Single(compilation.Warnings).Code);
    }

    [Fact]
    public async Task TheDefaultCertificateIsTheNamedOneThenTheFallbackAndAnUnusableNamedOneWarns()
    {
        CertificateId fallback = Add("fallback", ["localhost"], Now.AddDays(60), fallbackDefault: true);
        CertificateId named = Add("named", ["named.test"], Now.AddDays(60));
        ConfigDocument none = Document(Host("a", ["a.test"]));

        Assert.Same(_source.ContextOf(fallback), (await CompileAsync(none)).Snapshot!.Certificates.Default!.Certificate.Context);
        Assert.Same(_source.ContextOf(named), (await CompileAsync(none with { Settings = new GlobalSettings(DefaultCertificateId: named) })).Snapshot!.Certificates.Default!.Certificate.Context);

        ConfigCompilation unusable = await CompileAsync(none with { Settings = new GlobalSettings(DefaultCertificateId: Id<CertificateId>(99)) });
        Assert.Same(_source.ContextOf(fallback), unusable.Snapshot!.Certificates.Default!.Certificate.Context);
        ConfigWarning warning = Assert.Single(unusable.Warnings, w => w.Code == ConfigWarningCodes.CertificateDefaultUnusable);
        Assert.Equal("/settings/defaultCertificateId", warning.Path);
    }

    [Fact]
    public async Task WithoutAFallbackAnUnnamedDefaultMeansNoDefault()
    {
        Add("a", ["a.test"], Now.AddDays(60));

        ConfigCompilation compilation = await CompileAsync(Document(Host("x", ["x.test"])));

        Assert.Null(compilation.Snapshot!.Certificates.Default);
    }

    [Fact]
    public async Task AnUnchangedCertificateKeepsItsContextAcrossCompilesAndARenewalSwapsIt()
    {
        CertificateId id = Add("a", ["a.example.com"], Now.AddDays(60));
        ConfigDocument document = Document(Host("a", ["a.example.com"]));

        SslStreamCertificateContext first = Served(await CompileAsync(document), "a.example.com")!;
        SslStreamCertificateContext second = Served(await CompileAsync(document), "a.example.com")!;
        Assert.Same(first, second);

        _source.Replace(id, NewContext(["a.example.com"]));
        Assert.NotSame(first, Served(await CompileAsync(document), "a.example.com"));
    }

    [Fact]
    public async Task DisabledHostsAndHostsWithoutHttpsNeedNoCertificate()
    {
        Add("a", ["a.example.com"], Now.AddDays(60));
        HostConfig disabled = Host("off", ["off.example.com"]) with { Enabled = false };
        HostConfig none = Host("none", ["none.example.com"], new CertificateBinding { Mode = CertificateBindingMode.None });
        // A disabled host's Specific binding to a valid certificate is fine: nothing is served, so it needn't be loaded.
        HostConfig disabledSpecific = Host("offSpecific", ["off2.example.com"], new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = Label<CertificateId>("a") }) with { Enabled = false };

        ConfigCompilation compilation = await CompileAsync(Document(disabled, none, disabledSpecific));

        Assert.True(compilation.Succeeded);
        Assert.Empty(compilation.Warnings);
        Assert.Empty(_source.Loaded);
    }

    [Fact]
    public async Task ADisabledHostBoundToAnExpiredCertificateStillWarns()
    {
        CertificateId expired = Add("expired", ["a.example.com"], Now.AddDays(-1), notBefore: Now.AddDays(-90));
        HostConfig disabled = Host("off", ["a.example.com"], new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = expired }) with { Enabled = false };

        ConfigCompilation compilation = await CompileAsync(Document(disabled));

        Assert.True(compilation.Succeeded);
        Assert.Equal(ConfigWarningCodes.CertificateSpecificUnusable, Assert.Single(compilation.Warnings).Code);
    }

    [Theory]
    [InlineData("192.0.2.7", "192.0.2.7")]
    [InlineData("[2001:DB8::1]", "2001:db8::1")]
    [InlineData("2001:db8::0001", "2001:db8::1")]
    [InlineData("WWW.Example.com.", "www.example.com")]
    [InlineData("*.Example.com", "*.example.com")]
    [InlineData("1.2", null)] // numbers that aren't four-part addresses
    [InlineData("16909060", null)]
    [InlineData("1.2.3.4.5", null)]
    [InlineData("not a name", null)]
    [InlineData("a_b@example.com", null)]
    public void NamesAreNormalisedLikeHostNamesAndIpAddressesStrictly(string name, string? expected) =>
        Assert.Equal(expected, CertificateNames.Normalize(name));

    [Theory]
    [InlineData("www.example.com", "*.example.com")]
    [InlineData("*.example.com", null)]
    [InlineData("example", null)]
    [InlineData("192.0.2.7", null)] // an address has no wildcard
    [InlineData("2001:db8::1", null)]
    public void OnlyDnsNamesHaveAWildcard(string name, string? expected) => Assert.Equal(expected, CertificateNames.WildcardOf(name));

    private CertificateId Add(
        string label, string[] names, DateTimeOffset notAfter, DateTimeOffset? notBefore = null, bool fallbackDefault = false)
    {
        CertificateId id = Label<CertificateId>(label);
        CertificateInfo info = new(
            $"CN={label}", names, notBefore ?? Now.AddDays(-30), notAfter, "CN=Test", "01", null, label, "ECDSA P-256", [], string.Empty);
        _builtIns.Entries.Add(new BuiltInCertificate(id, info, fallbackDefault));
        _source.Contexts[id] = NewContext(names);
        return id;
    }

    private SslStreamCertificateContext NewContext(string[] names)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=test", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        foreach (string name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _owned.Add(certificate);
        return SslStreamCertificateContext.Create(certificate, null, offline: true);
    }

    private async Task<ConfigCompilation> CompileAsync(ConfigDocument document)
    {
        CertificateInventoryLoader loader = new(_source, _builtIns, _clock);
        CertificateInventory inventory = await loader.LoadAsync(null, document, TestContext.Current.CancellationToken);
        return new ConfigCompiler(SnapshotCompilerOptions.Default).Compile(document, Revision, inventory);
    }

    private static SslStreamCertificateContext? Served(ConfigCompilation compilation, string name) =>
        compilation.Snapshot!.Certificates.Find(name)?.Certificate.Context;

    private static InventoryCertificate Entry(string label, string[] names, DateTimeOffset notAfter, DateTimeOffset? notBefore = null) =>
        new(Label<CertificateId>(label), names, notBefore ?? Now.AddDays(-30), notAfter, null, null);

    private static HostConfig Host(string label, string[] names, CertificateBinding? binding = null) =>
        new(Label<HostId>(label), names, Kind: HostKind.Respond, Certificate: binding, Respond: new RespondConfig(200, "x"));

    private static readonly Dictionary<(Type, string), int> Labels = [];

    /// <summary>The same ID for the same label, a different one for another.</summary>
    private static T Label<T>(string label)
        where T : struct, Paduraksa.Core.Ids.ITypedId<T, Guid>
    {
        lock (Labels)
        {
            if (!Labels.TryGetValue((typeof(T), label), out int n))
            {
                Labels[(typeof(T), label)] = n = Labels.Count + 1000;
            }

            return Id<T>(n);
        }
    }

    private static ConfigDocument Document(params HostConfig[] hosts) => new(Hosts: hosts);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BuiltIns : IBuiltInCertificates
    {
        public List<BuiltInCertificate> Entries { get; } = [];

        public IReadOnlyList<BuiltInCertificate> Current => Entries;
    }

    private sealed class Source : IServerCertificateSource
    {
        public Dictionary<CertificateId, SslStreamCertificateContext> Contexts { get; } = [];

        public HashSet<CertificateId> Missing { get; } = [];

        public HashSet<CertificateId> Throwing { get; } = [];

        public List<CertificateId> Loaded { get; } = [];

        public SslStreamCertificateContext ContextOf(CertificateId id) => Contexts[id];

        public void Replace(CertificateId id, SslStreamCertificateContext context) => Contexts[id] = context;

        public ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken)
        {
            if (Throwing.Contains(id))
            {
                throw new InvalidOperationException("the vault is unwell");
            }

            if (Missing.Contains(id) || !Contexts.TryGetValue(id, out SslStreamCertificateContext? context))
            {
                return ValueTask.FromResult<SslStreamCertificateContext?>(null);
            }

            Loaded.Add(id);
            return ValueTask.FromResult<SslStreamCertificateContext?>(context);
        }
    }
}
