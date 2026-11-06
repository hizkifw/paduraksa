using System.Net;
using System.Security.Authentication;

using Microsoft.AspNetCore.Connections;

using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Tls;

public sealed class TlsHandshakeSelectorTests : IClassFixture<CertificateTableTests.Certs>
{
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.0.2.10"), 50_000);

    private readonly CertificateTableTests.Certs _certs;

    public TlsHandshakeSelectorTests(CertificateTableTests.Certs certs) => _certs = certs;

    private CertificateTable Table(bool withDefault)
    {
        var builder = new CertificateTableBuilder()
            .Add(["a.test"], _certs.A, TlsPolicy.Default)
            .Add(["tcp-only.test"], _certs.B, new TlsPolicy { Http3Enabled = false })
            .Add(["*.wild.test"], _certs.Wild, TlsPolicy.Default);
        if (withDefault)
        {
            builder.SetDefault(_certs.Default, TlsPolicy.Default);
        }

        return builder.Build();
    }

    [Fact]
    public void TcpSelectsThePrebuiltOptions()
    {
        var table = Table(withDefault: true);
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table));

        Assert.Same(table.Find("a.test")!.TcpOptions, selector.SelectTcp("a.test"));
        Assert.Same(table.Find("x.wild.test")!.TcpOptions, selector.SelectTcp("X.Wild.Test"));
        Assert.Same(table.Default!.TcpOptions, selector.SelectTcp("unknown.test"));
        Assert.Same(table.Default!.TcpOptions, selector.SelectTcp(""));
        Assert.Same(table.Default!.TcpOptions, selector.SelectTcp(null));
        Assert.Same(table.Find("tcp-only.test")!.TcpOptions, selector.SelectTcp("tcp-only.test"));
    }

    [Theory]
    [InlineData("unknown.test")]
    [InlineData("a.b.wild.test")]
    [InlineData("")]
    public void TcpRejectsUnknownNamesByThrowingAndCounts(string sni)
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)));

        var error = Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectTcp(sni));

        Assert.IsAssignableFrom<AuthenticationException>(error);
        Assert.Equal(TlsRejectionReason.UnknownServerName, error.Reason);
        Assert.Equal(sni, error.ServerName);
        if (sni.Length > 0)
        {
            Assert.DoesNotContain(sni, error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, selector.RejectedUnknownServerNameTcp);
        Assert.Equal(0, selector.RejectedUnknownServerNameQuic);
    }

    [Fact]
    public void QuicSelectsThePrebuiltOptions()
    {
        var table = Table(withDefault: true);
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table));

        Assert.Same(table.Find("a.test")!.QuicOptions, selector.SelectQuic("a.test", Client, null));
        Assert.Same(table.Default!.QuicOptions, selector.SelectQuic("unknown.test", Client, null));
        Assert.Same(table.Default!.QuicOptions, selector.SelectQuic("", null, null));
    }

    [Fact]
    public void QuicRejectsHostsThatOptedOutOfHttp3EvenWithADefault()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: true)));

        var error = Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("tcp-only.test", Client, null));

        Assert.Equal(TlsRejectionReason.Http3Disabled, error.Reason);
        Assert.Equal(1, selector.RejectedHttp3Disabled);
        Assert.Equal(0, selector.RejectedUnknownServerNameQuic);
    }

    [Fact]
    public void QuicDefaultThatOptedOutOfHttp3RejectsUnknownNames()
    {
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _certs.A, TlsPolicy.Default)
            .SetDefault(_certs.Default, new TlsPolicy { Http3Enabled = false })
            .Build();
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table));

        Assert.NotNull(selector.SelectQuic("a.test", Client, null));
        Assert.Equal(TlsRejectionReason.Http3Disabled, Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("other.test", Client, null)).Reason);
        Assert.Same(table.Default!.TcpOptions, selector.SelectTcp("other.test"));
    }

    [Fact]
    public void QuicRejectsUnknownNamesWithoutDefault()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)));

        var error = Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("unknown.test", Client, null));

        Assert.Equal(TlsRejectionReason.UnknownServerName, error.Reason);
        Assert.Equal(1, selector.RejectedUnknownServerNameQuic);
        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);
    }

    [Fact]
    public void QuicGateRunsBeforeCertificateSelection()
    {
        var seen = new List<IPEndPoint?>();
        var selector = new TlsHandshakeSelector(
            new CertificateTableSource(Table(withDefault: false)),
            (remote, _) =>
            {
                seen.Add(remote);
                return !Equals(remote?.Address, Client.Address);
            });
        var allowed = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 443);

        // Denied even for a known name, and the gate wins over "unknown name" too.
        Assert.Equal(TlsRejectionReason.ConnectionDenied, Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("a.test", Client, null)).Reason);
        Assert.Equal(TlsRejectionReason.ConnectionDenied, Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("unknown.test", Client, null)).Reason);
        Assert.NotNull(selector.SelectQuic("a.test", allowed, null));
        Assert.Equal(TlsRejectionReason.UnknownServerName, Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic("unknown.test", allowed, null)).Reason);

        Assert.Equal([Client, Client, allowed, allowed], seen);
        Assert.Equal(2, selector.RejectedConnectionDenied);
        Assert.Equal(1, selector.RejectedUnknownServerNameQuic);
    }

    [Fact]
    public void TcpDoesNotRunTheQuicGate()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)), static (_, _) => false);

        Assert.NotNull(selector.SelectTcp("a.test"));
        Assert.Equal(0, selector.RejectedConnectionDenied);
    }

    [Fact]
    public void SwappedTableIsSeenByTheNextHandshake()
    {
        var source = new CertificateTableSource(Table(withDefault: false));
        var selector = new TlsHandshakeSelector(source);
        Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectTcp("new.test"));

        var next = new CertificateTableBuilder().Add(["new.test"], _certs.B, TlsPolicy.Default).Build();
        var previous = source.Swap(next);

        Assert.NotSame(next, previous);
        Assert.Same(next.Find("new.test")!.TcpOptions, selector.SelectTcp("new.test"));
        Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectTcp("a.test"));
    }

    [Fact]
    public void CallbackOptionsCarryTheSelectorAndSharedDelegates()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(CertificateTable.Empty));

        var tcp = selector.CreateTcpCallbackOptions();
        var quic = selector.CreateQuicCallbackOptions();

        Assert.Same(selector, tcp.OnConnectionState);
        Assert.Same(selector, quic.OnConnectionState);
        Assert.NotSame(tcp.OnConnection, quic.OnConnection);
        Assert.Same(tcp.OnConnection, selector.CreateTcpCallbackOptions().OnConnection); // static, not a closure per call
        Assert.Same(quic.OnConnection, new TlsHandshakeSelector(new CertificateTableSource(CertificateTable.Empty)).CreateQuicCallbackOptions().OnConnection);
    }

    [Theory]
    [InlineData("a.test")]
    [InlineData("x.wild.test")]
    [InlineData("unknown.test")]
    [InlineData("")]
    public void TcpSelectionDoesNotAllocate(string sni)
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: true)));
        AllocAssert.Zero((selector, sni), static s => s.selector.SelectTcp(s.sni), $"SelectTcp(\"{sni}\")");
    }

    [Theory]
    [InlineData("a.test")]
    [InlineData("x.wild.test")]
    [InlineData("unknown.test")]
    public void QuicSelectionWithGateDoesNotAllocate(string sni)
    {
        var selector = new TlsHandshakeSelector(
            new CertificateTableSource(Table(withDefault: true)),
            static (remote, _) => remote is not null && remote.Port != 0);
        AllocAssert.Zero((selector, sni), static s => s.selector.SelectQuic(s.sni, Client, null), $"SelectQuic(\"{sni}\")");
    }

    [Fact]
    public async Task GateReceivesTheConnection()
    {
        ConnectionContext? seen = null;
        await using var connection = new DefaultConnectionContext("test");
        var selector = new TlsHandshakeSelector(
            new CertificateTableSource(Table(withDefault: true)),
            (_, c) =>
            {
                seen = c;
                return true;
            });

        selector.SelectQuic("a.test", Client, connection);

        Assert.Same(connection, seen);
    }
}
