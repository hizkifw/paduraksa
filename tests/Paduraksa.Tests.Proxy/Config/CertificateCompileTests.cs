using System.Diagnostics.CodeAnalysis;
using System.Net.Security;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Tls;

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

    private static ProxyConfigModel Model(string? defaultCertificate = "fallback") => new()
    {
        Hosts =
        [
            Respond("a", ["a.test"]) with { Certificate = "a" },
            Respond("b", ["b.test", "*.b.test"]) with { Certificate = "b", Tls = new HostTlsSpec { Http3 = false } },
            Respond("plain", ["plain.test", "192.0.2.10", "[2001:db8::10]"]) with { Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 } },
            Respond("off", ["off.test"]) with { Certificate = "a", Enabled = false },
        ],
        Defaults = new DefaultsSpec { Certificate = defaultCertificate },
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
        CertificateTable table = Compile(Model(defaultCertificate: null), _options).Certificates;

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

    public static TheoryData<string, ProxyConfigModel, bool, string> InvalidReferences => new()
    {
        { "unknown host certificate", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = "nope" }] }, true, "host 'a': certificate 'nope' doesn't exist" },
        { "unknown default", new ProxyConfigModel { Defaults = new DefaultsSpec { Certificate = "nope" } }, true, "defaults: certificate 'nope' doesn't exist" },
        { "blank reference", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = " " }] }, true, "can't be empty" },
        { "disabled hosts are validated too", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = "nope", Enabled = false }] }, true, "doesn't exist" },
        { "no resolver", new ProxyConfigModel { Hosts = [Respond("a", ["a.test"]) with { Certificate = "a" }] }, false, "no certificate source" },
    };

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public void UnresolvableReferencesFailTheCompile(string because, ProxyConfigModel model, bool withResolver, string expected)
    {
        CompileResult result = SnapshotCompiler.Compile(model, withResolver ? _options : null);

        Assert.False(result.Succeeded, because);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
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

        // A renewed certificate under the same reference goes live with the next publish (new handshakes only).
        ServerCertificate renewed = _pki.Leaf("a.test v2", "a.test");
        _certificates.Set("a", renewed);
        store.Publish(Bound(Model(), generation: 2, options: _options));
        Assert.Same(renewed.Context, selector.SelectTcp("a.test").ServerCertificateContext);
        Assert.Same(renewed.Context, selector.SelectQuic("a.test", null, null).ServerCertificateContext);
        Assert.Equal(1, selector.RejectedUnknownServerNameTcp);
    }

    private static HostSpec Respond(string key, string[] names) =>
        new() { Key = key, Names = names, Kind = HostKind.Respond, Respond = new RespondSpec { Body = key } };

    /// <summary>A test certificate store: reference → certificate, replaceable.</summary>
    private sealed class Certificates : IServerCertificateResolver
    {
        private readonly Dictionary<string, ServerCertificate> _byReference = new(StringComparer.Ordinal);

        public ServerCertificate this[string reference] => _byReference[reference];

        public void Set(string reference, ServerCertificate certificate) => _byReference[reference] = certificate;

        public bool TryResolve(string reference, [NotNullWhen(true)] out ServerCertificate? certificate) =>
            _byReference.TryGetValue(reference, out certificate);
    }
}
