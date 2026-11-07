using System.Net.Security;
using System.Security.Authentication;

using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Tls;

public sealed class CertificateTableTests : IClassFixture<CertificateTableTests.Certs>
{
    private readonly Certs _certs;

    public CertificateTableTests(Certs certs) => _certs = certs;

    private CertificateTable Table(bool withDefault = true)
    {
        var builder = new CertificateTableBuilder()
            .Add(["a.test"], _certs.A, TlsPolicy.Default)
            .Add(["b.test", "www.b.test"], _certs.B, TlsPolicy.Default)
            .Add(["*.wild.test"], _certs.Wild, TlsPolicy.Default)
            .Add(["exact.wild.test"], _certs.A, TlsPolicy.Default)
            .Add(["xn--bcher-kva.test"], _certs.B, TlsPolicy.Default)
            .Add(["*.xn--caf-dma.test"], _certs.Wild, TlsPolicy.Default);
        if (withDefault)
        {
            builder.SetDefault(_certs.Default, TlsPolicy.Default);
        }

        return builder.Build();
    }

    [Theory]
    [InlineData("a.test", "a")]
    [InlineData("b.test", "b")]
    [InlineData("www.b.test", "b")]
    [InlineData("A.TEST", "a")]
    [InlineData("Www.B.Test", "b")]
    [InlineData("a.test.", "a")]
    [InlineData("x.wild.test", "wild")]
    [InlineData("X.WILD.TEST.", "wild")]
    [InlineData("exact.wild.test", "a")] // exact beats wildcard
    [InlineData("wild.test", "default")] // the wildcard doesn't cover its parent
    [InlineData("a.b.wild.test", "default")] // ... nor more than one extra label
    [InlineData("unknown.test", "default")]
    [InlineData("", "default")] // no SNI
    [InlineData(".", "default")]
    [InlineData("a.test..", "default")] // only one trailing dot is dropped
    public void FindsExactThenWildcardThenDefault(string sni, string expected)
    {
        var entry = Table().Find(sni);

        Assert.NotNull(entry);
        Assert.Equal(_certs.Name(entry.Certificate), expected);
    }

    [Theory]
    [InlineData("unknown.test")]
    [InlineData("a.b.wild.test")]
    [InlineData("")]
    public void WithoutDefaultUnknownNamesFindNothing(string sni) =>
        Assert.Null(Table(withDefault: false).Find(sni));

    [Fact]
    public void ConfiguredNamesAreNormalized()
    {
        var table = new CertificateTableBuilder()
            .Add(["App.Example.COM.", "*.Wild.Example.com"], _certs.A, TlsPolicy.Default)
            .Build();

        Assert.Equal(["app.example.com"], table.ExactNames);
        Assert.Equal(["wild.example.com"], table.WildcardParents);
        Assert.Same(_certs.A, table.Find("app.example.com")!.Certificate);
        Assert.Same(_certs.A, table.Find("x.wild.example.com")!.Certificate);
    }

    // TCP (SslStream) hands the callback the Unicode form of an IDN SNI; QUIC passes punycode through.
    [Theory]
    [InlineData("xn--bcher-kva.test", "a")]
    [InlineData("bücher.test", "a")]
    [InlineData("BÜCHER.TEST.", "a")]
    [InlineData("x.xn--caf-dma.test", "wild")]
    [InlineData("x.café.test", "wild")]
    [InlineData("bücher.café.test", "wild")] // a Unicode covered label still matches (no ASCII-label check here)
    [InlineData("xn--caf-dma.test", "default")] // the wildcard doesn't cover its parent, in either form
    [InlineData("café.test", "default")]
    [InlineData("a.b.café.test", "default")]
    public void IdnNamesMatchPunycodeAndUnicodeSni(string sni, string expected)
    {
        var table = new CertificateTableBuilder()
            .Add(["XN--BCHER-KVA.test"], _certs.A, TlsPolicy.Default)
            .Add(["*.xn--caf-dma.test"], _certs.Wild, TlsPolicy.Default)
            .SetDefault(_certs.Default, TlsPolicy.Default)
            .Build();

        Assert.Equal(["xn--bcher-kva.test"], table.ExactNames); // aliases aren't configured names
        Assert.Equal(["xn--caf-dma.test"], table.WildcardParents);
        Assert.Equal(expected, _certs.Name(table.Find(sni)!.Certificate));
    }

    [Fact]
    public void IdnAliasDoesNotHideDuplicates() =>
        Assert.Throws<ArgumentException>(() => new CertificateTableBuilder()
            .Add(["xn--bcher-kva.test"], _certs.A, TlsPolicy.Default)
            .Add(["XN--BCHER-KVA.TEST."], _certs.B, TlsPolicy.Default));

    [Fact]
    public void UndecodablePunycodeIsKeptAsConfigured()
    {
        // The decoder (and so SslStream) returns undecodable labels unchanged: no alias, and the name still matches.
        var table = new CertificateTableBuilder()
            .Add(["xn--zzzzzzzzzzzzzzzzzzzzzz.test", "*.xn--zzzzzzzzzzzzzzzzzzzzzz.wild"], _certs.A, TlsPolicy.Default)
            .Build();

        Assert.Same(_certs.A, table.Find("xn--zzzzzzzzzzzzzzzzzzzzzz.test")!.Certificate);
        Assert.Same(_certs.A, table.Find("x.xn--zzzzzzzzzzzzzzzzzzzzzz.wild")!.Certificate);
    }

    [Fact]
    public void EmptyTableRejectsEverything()
    {
        Assert.Null(CertificateTable.Empty.Find("a.test"));
        Assert.Null(CertificateTable.Empty.Find(""));
        Assert.Empty(CertificateTable.Empty.Options);
    }

    [Theory]
    [InlineData("a.test", "A.TEST")]
    [InlineData("a.test", "a.test.")]
    [InlineData("*.wild.test", "*.WILD.test.")]
    public void DuplicateNamesAcrossHostsAreRejected(string first, string second)
    {
        var builder = new CertificateTableBuilder().Add([first], _certs.A, TlsPolicy.Default);

        var error = Assert.Throws<ArgumentException>(() => builder.Add([second], _certs.B, TlsPolicy.Default));
        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
        Assert.Contains(second, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNamesWithinOneHostAreRejected() =>
        Assert.Throws<ArgumentException>(() => new CertificateTableBuilder().Add(["a.test", "A.test."], _certs.A, TlsPolicy.Default));

    [Fact]
    public void ExactNameAndWildcardOfTheSameParentCoexist()
    {
        var table = new CertificateTableBuilder()
            .Add(["wild.test"], _certs.A, TlsPolicy.Default)
            .Add(["*.wild.test"], _certs.Wild, TlsPolicy.Default)
            .Build();

        Assert.Same(_certs.A, table.Find("wild.test")!.Certificate);
        Assert.Same(_certs.Wild, table.Find("x.wild.test")!.Certificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("*.*.test")]
    [InlineData("**.test")]
    [InlineData("a.*.test")]
    [InlineData("a*.test")]
    [InlineData("a..test")]
    [InlineData(".a.test")]
    [InlineData("-a.test")]
    [InlineData("a-.test")]
    [InlineData("a b.test")]
    [InlineData("a:443")]
    [InlineData("[::1]")]
    [InlineData("bücher.test")]
    [InlineData("192.168.1.10")] // IP-like: the last label is numeric
    [InlineData("10.0.0.1.")]
    [InlineData("host.123")]
    [InlineData("*.1.2")]
    [InlineData("*.com")] // wildcard parent needs two labels
    [InlineData("*.lan.")]
    public void InvalidNamesAreRejected(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => new CertificateTableBuilder().Add([name], _certs.A, TlsPolicy.Default));
        Assert.Contains("Invalid server name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123.example.com")] // numeric labels are fine, just not the last one
    [InlineData("localhost")] // single-label exact names are allowed
    [InlineData("*.home.arpa")]
    [InlineData("a-1.b2.c3d")]
    public void EdgeCaseNamesAreAccepted(string name) =>
        Assert.NotNull(new CertificateTableBuilder().Add([name], _certs.A, TlsPolicy.Default).Build());

    [Fact]
    public void OverlongNamesAndLabelsAreRejected()
    {
        var longLabel = new string('a', 64) + ".test";
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".test";

        Assert.Throws<ArgumentException>(() => new CertificateTableBuilder().Add([longLabel], _certs.A, TlsPolicy.Default));
        Assert.Throws<ArgumentException>(() => new CertificateTableBuilder().Add([longName], _certs.A, TlsPolicy.Default));
    }

    [Fact]
    public void HostWithoutNamesIsRejected() =>
        Assert.Throws<ArgumentException>(() => new CertificateTableBuilder().Add([], _certs.A, TlsPolicy.Default));

    [Fact]
    public void FailedAddLeavesBuilderUnchanged()
    {
        var builder = new CertificateTableBuilder();

        Assert.Throws<ArgumentException>(() => builder.Add(["ok.test", "not ok.test"], _certs.A, TlsPolicy.Default));
        builder.Add(["ok.test"], _certs.B, TlsPolicy.Default);

        Assert.Same(_certs.B, builder.Build().Find("ok.test")!.Certificate);
    }

    [Fact]
    public void SameCertificateAndEqualPolicyShareOptions()
    {
        // Equal but distinct policy instances (records compare by value).
        var policy1 = new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13 };
        var policy2 = new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13 };
        var table = new CertificateTableBuilder()
            .Add(["one.wild.test"], _certs.Wild, policy1)
            .Add(["two.wild.test"], _certs.Wild, policy2)
            .Add(["*.wild.test"], _certs.Wild, TlsPolicy.Default)
            .Add(["other.test"], _certs.A, policy1)
            .SetDefault(_certs.Wild, TlsPolicy.Default)
            .Build();

        var one = table.Find("one.wild.test")!;
        var two = table.Find("two.wild.test")!;
        var wildcard = table.Find("x.wild.test")!;
        var other = table.Find("other.test")!;

        Assert.Same(one, two);
        Assert.Same(one.TcpOptions, two.TcpOptions);
        Assert.Same(one.QuicOptions, two.QuicOptions);
        Assert.Same(wildcard, table.Default); // the default (same cert + policy) is the wildcard's entry too
        Assert.NotSame(one.TcpOptions, wildcard.TcpOptions); // same cert, different policy
        Assert.NotSame(one.TcpOptions, other.TcpOptions); // same policy, different cert
        Assert.Same(one.Certificate.Context, one.TcpOptions.ServerCertificateContext);
        Assert.Same(one.TcpOptions.ServerCertificateContext, one.QuicOptions!.ServerCertificateContext);

        Assert.Equal(3, table.Entries.Count);
        Assert.Equal(6, table.Options.Count);
    }

    [Fact]
    public void ReplacedDefaultIsDropped()
    {
        var table = new CertificateTableBuilder()
            .SetDefault(_certs.A, TlsPolicy.Default)
            .SetDefault(_certs.Default, TlsPolicy.Default)
            .Build();

        Assert.Same(_certs.Default, table.Default!.Certificate);
        Assert.Single(table.Entries);
    }

    [Fact]
    public void EveryOptionsObjectHasExplicitAlpn()
    {
        var clientCertificates = new ClientCertificatePolicy();
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _certs.A, TlsPolicy.Default)
            .Add(["b.test"], _certs.B, new TlsPolicy { TcpAlpn = TcpAlpn.Http11Only })
            .Add(["*.wild.test"], _certs.Wild, new TlsPolicy { Http3Enabled = false })
            .Add(["c.test"], _certs.A, new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13, ClientCertificate = clientCertificates })
            .SetDefault(_certs.Default, TlsPolicy.Default)
            .Build();

        Assert.Equal(9, table.Options.Count);
        foreach (var options in table.Options)
        {
            Assert.NotNull(options.ApplicationProtocols);
            Assert.NotEmpty(options.ApplicationProtocols);
            Assert.NotNull(options.ServerCertificateContext);
            Assert.Null(options.ServerCertificate);
        }

        foreach (var entry in table.Entries)
        {
            var expectedTcp = entry.Policy.TcpAlpn == TcpAlpn.Http11Only
                ? new[] { SslApplicationProtocol.Http11 }
                : [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11];
            Assert.Equal(expectedTcp, entry.TcpOptions.ApplicationProtocols!);
            if (entry.Policy.Http3Enabled)
            {
                Assert.Equal([SslApplicationProtocol.Http3], entry.QuicOptions!.ApplicationProtocols!);
            }
            else
            {
                Assert.Null(entry.QuicOptions);
            }
        }
    }

    [Fact]
    public void MinimumVersionMapsToEnabledProtocolsOnTcpOnly()
    {
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _certs.A, TlsPolicy.Default)
            .Add(["b.test"], _certs.B, new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13 })
            .Build();

        var tls12 = table.Find("a.test")!;
        var tls13 = table.Find("b.test")!;
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, tls12.TcpOptions.EnabledSslProtocols);
        Assert.Equal(SslProtocols.Tls13, tls13.TcpOptions.EnabledSslProtocols);
        Assert.False(tls12.TcpOptions.AllowRenegotiation);
        Assert.Equal(SslProtocols.None, tls13.QuicOptions!.EnabledSslProtocols); // QUIC is TLS 1.3 by definition
    }

    [Fact]
    public void ClientCertificatePolicyMapsToValidationCallback()
    {
        var acceptAll = new RemoteCertificateValidationCallback(static (_, certificate, _, _) => certificate is not null);
        var table = new CertificateTableBuilder()
            .Add(["none.test"], _certs.A, TlsPolicy.Default)
            .Add(["required.test"], _certs.A, new TlsPolicy { ClientCertificate = new ClientCertificatePolicy { Validator = acceptAll } })
            .Add(["optional.test"], _certs.A, new TlsPolicy { ClientCertificate = new ClientCertificatePolicy { Required = false } })
            .Build();

        var none = table.Find("none.test")!;
        Assert.False(none.TcpOptions.ClientCertificateRequired);
        Assert.Null(none.TcpOptions.RemoteCertificateValidationCallback);

        foreach (var options in new[] { table.Find("required.test")!.TcpOptions, table.Find("required.test")!.QuicOptions! })
        {
            Assert.True(options.ClientCertificateRequired);
            var validate = options.RemoteCertificateValidationCallback!;
            Assert.False(validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.True(validate(this, _certs.A.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors)); // validator decides
        }

        var optional = table.Find("optional.test")!.TcpOptions.RemoteCertificateValidationCallback!;
        Assert.True(optional(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(optional(this, _certs.A.Leaf, null, SslPolicyErrors.RemoteCertificateChainErrors)); // platform validation
        Assert.True(optional(this, _certs.A.Leaf, null, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData("a.test")] // exact
    [InlineData("A.Test.")] // case + trailing dot
    [InlineData("x.wild.test")] // wildcard
    [InlineData("a.b.wild.test")] // wildcard miss → default
    [InlineData("unknown.test")] // default
    [InlineData("")] // no SNI
    [InlineData("nodots")]
    [InlineData("bücher.test")] // IDN as SslStream decodes it (alias of xn--bcher-kva.test)
    [InlineData("x.café.test")] // IDN wildcard parent
    public void LookupsDoNotAllocate(string sni) =>
        AllocAssert.Zero((Table(), sni), static s => s.Item1.Find(s.sni), $"Find(\"{sni}\")");

    public sealed class Certs : IDisposable
    {
        private readonly TestPki _pki = TestPki.Create();

        public Certs()
        {
            A = _pki.Leaf("a.test", "a.test");
            B = _pki.Leaf("b.test", "b.test", "www.b.test");
            Wild = _pki.Leaf("*.wild.test", "*.wild.test");
            Default = _pki.Leaf("default.test", "default.test");
        }

        public ServerCertificate A { get; }

        public ServerCertificate B { get; }

        public ServerCertificate Wild { get; }

        public ServerCertificate Default { get; }

        public string Name(ServerCertificate certificate) =>
            ReferenceEquals(certificate, A) ? "a"
            : ReferenceEquals(certificate, B) ? "b"
            : ReferenceEquals(certificate, Wild) ? "wild"
            : ReferenceEquals(certificate, Default) ? "default"
            : "?";

        public void Dispose() => _pki.Dispose();
    }
}
