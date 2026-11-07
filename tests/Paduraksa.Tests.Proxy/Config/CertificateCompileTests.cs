using System.Diagnostics.CodeAnalysis;
using System.Net.Security;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Tls;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Config;

/// <summary>The compiler builds the snapshot's certificate table; the store serves it to the handshake selector.</summary>
public sealed class CertificateCompileTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly Certificates _certificates = new();
    private readonly SnapshotCompilerOptions _options;

    public CertificateCompileTests()
    {
        _certificates.Set("a", _pki.Leaf("a.test", "a.test"));
        _certificates.Set("b", _pki.Leaf("b.test", "b.test", "*.b.test"));
        _certificates.Set("fallback", _pki.Leaf("fallback", "fallback.test"));
        _options = new SnapshotCompilerOptions { Certificates = _certificates };
    }

    public void Dispose() => _pki.Dispose();

    private static ProxyConfigModel Model(bool withDefault = true) => new()
    {
        Hosts =
        [
            Respond("a", ["a.test"]) with { Certificate = TestIds.Certificate("a") },
            Respond("b", ["b.test", "*.b.test"]) with { Certificate = TestIds.Certificate("b"), Tls = new HostTlsSpec { Http3 = false } },
            Respond("plain", ["plain.test", "192.0.2.10", "[2001:db8::10]"]) with { Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 } },
            Respond("off", ["off.test"]) with { Certificate = TestIds.Certificate("a"), Enabled = false },
        ],
        Defaults = new DefaultsSpec { Certificate = withDefault ? TestIds.Certificate("fallback") : null },
    };

    [Fact]
    public void HostsGetTheirCertificatesAndTheDefaultServesTheRest()
    {
        CertificateTable table = Compile(Model(), _options).Certificates;

        Assert.Same(_certificates["a"], table.Find("a.test")!.Certificate);
        Assert.Same(_certificates["b"], table.Find("x.b.test")!.Certificate);
        Assert.Same(_certificates["fallback"], table.Find("unknown.test")!.Certificate);
        Assert.Same(_certificates["fallback"], table.Find(string.Empty)!.Certificate);

        // Per-host TLS settings travel with the entry, also for a host on the default certificate.
        Assert.Null(table.Find("b.test")!.QuicOptions);
        CertificateTableEntry plain = table.Find("plain.test")!;
        Assert.Same(_certificates["fallback"], plain.Certificate);
        Assert.Equal(TlsMinimumVersion.Tls13, plain.Policy.MinimumVersion);
        Assert.NotSame(table.Default, plain);

        // IP literals never arrive as SNI and aren't table names; disabled hosts take no names.
        Assert.Equal(["a.test", "b.test", "plain.test"], table.ExactNames.Order(StringComparer.Ordinal));
        Assert.Equal(["b.test"], table.WildcardParents);
        Assert.Same(table.Default, table.Find("off.test"));
    }

    [Fact]
    public void WithoutADefaultUnknownNamesAndCertificatelessHostsAreRejected()
    {
        CertificateTable table = Compile(Model(withDefault: false), _options).Certificates;

        Assert.Null(table.Default);
        Assert.Null(table.Find("unknown.test"));
        Assert.Null(table.Find("plain.test"));
        Assert.NotNull(table.Find("a.test"));
    }

    [Fact]
    public void AConfigWithoutCertificatesCompilesWithoutAResolver()
    {
        RuntimeSnapshot snapshot = Compile(new ProxyConfigModel { Hosts = [Respond("a", ["a.test"])] });

        Assert.Null(snapshot.Certificates.Find("a.test"));
        Assert.Empty(snapshot.Certificates.Entries);
    }

    [Fact]
    public void AHostWithoutHttpsTakesNoCertificateEntry()
    {
        RuntimeSnapshot snapshot = Compile(
            new ProxyConfigModel
            {
                Hosts = [Respond("a", ["a.test"]) with { Certificate = TestIds.Certificate("a") }, Respond("bare", ["bare.test", "*.bare.test"]) with { Https = false }],
                Defaults = new DefaultsSpec { Certificate = TestIds.Certificate("fallback") },
            },
            _options with { AdvertiseHttp3 = true });

        Assert.Equal(["a.test"], snapshot.Certificates.ExactNames);
        Assert.Empty(snapshot.Certificates.WildcardParents);
        Assert.Same(snapshot.Certificates.Default, snapshot.Certificates.Find("bare.test"));
        CompiledHost bare = snapshot.Hosts.Find("bare.test")!;
        Assert.False(bare.Https);
        Assert.Equal(0, bare.AltSvc.Count);
        CompiledHost a = snapshot.Hosts.Find("a.test")!;
        Assert.True(a.Https);
        Assert.Equal(1, a.AltSvc.Count);
    }

    public static TheoryData<string, ProxyConfigModel, bool, string> InvalidCertificates => new()
    {
        { "unknown host certificate", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = TestIds.Certificate("nope") }] }, true, $"host '{TestIds.Host("a")}': certificate '{TestIds.Certificate("nope")}' doesn't exist" },
        { "unknown default", new ProxyConfigModel { Defaults = new DefaultsSpec { Certificate = TestIds.Certificate("nope") } }, true, $"defaults: certificate '{TestIds.Certificate("nope")}' doesn't exist" },
        { "empty ID", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = CertificateId.Empty }] }, true, "can't be empty" },
        { "disabled hosts are validated too", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = TestIds.Certificate("nope"), Enabled = false }] }, true, "doesn't exist" },
        { "no resolver", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = TestIds.Certificate("a") }] }, false, "no certificate source" },
        { "certificate without HTTPS", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Https = false, Certificate = TestIds.Certificate("a") }] }, true, $"host '{TestIds.Host("a")}': a host without HTTPS can't name a certificate" },
        { "forced HTTPS without HTTPS", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Https = false, ForceHttps = true }] }, true, $"host '{TestIds.Host("a")}': a host without HTTPS can't force HTTPS" },
    };

    [Theory]
    [MemberData(nameof(InvalidCertificates))]
    public void UnresolvableCertificatesFailTheCompile(string because, ProxyConfigModel model, bool withResolver, string expected)
    {
        CompileResult result = SnapshotCompiler.Compile(model, withResolver ? _options : null);

        Assert.False(result.Succeeded, because);
        Assert.Contains(result.Errors, e => e.ToString().Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePublishedSnapshotDrivesTheHandshakeSelector()
    {
        using SnapshotStore store = new();
        TlsHandshakeSelector selector = new(store);

        // The bootstrap snapshot has no certificates: everything is rejected.
        Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectTcp("a.test"));

        store.Publish(Bound(Model(), generation: 1, options: _options));
        SslServerAuthenticationOptions first = selector.SelectTcp("a.test");
        Assert.Same(_certificates["a"].Context, first.ServerCertificateContext);

        // A renewed certificate under the same ID goes live with the next publish (new handshakes only).
        ServerCertificate renewed = _pki.Leaf("a.test v2", "a.test");
        _certificates.Set("a", renewed);
        store.Publish(Bound(Model(), generation: 2, options: _options));
        Assert.Same(renewed.Context, selector.SelectTcp("a.test").ServerCertificateContext);
        Assert.Same(renewed.Context, selector.SelectQuic("a.test", null, null).ServerCertificateContext);
        Assert.Equal(1, selector.Counters.UnknownServerNameTcp);
    }

    private static HostSpec Respond(string label, string[] names) =>
        new() { Id = TestIds.Host(label), Names = names, Kind = HostKind.Respond, Respond = new RespondSpec { Body = label } };

    /// <summary>A test certificate store: ID → certificate, replaceable; addressed by <see cref="TestIds"/> label.</summary>
    private sealed class Certificates : IServerCertificateResolver
    {
        private readonly Dictionary<CertificateId, ServerCertificate> _byId = [];

        public ServerCertificate this[string label] => _byId[TestIds.Certificate(label)];

        public void Set(string label, ServerCertificate certificate) => _byId[TestIds.Certificate(label)] = certificate;

        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate) =>
            _byId.TryGetValue(id, out certificate);
    }
}
