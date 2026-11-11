using System.Net;
using System.Security.Authentication;

using CsCheck;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Simulation;

// The fixed parity tables above, widened: any request the generator can build is run through the real gate and through the
// simulator, under each unknown-host behaviour and trusted-proxy mode, and they must answer alike.
public sealed partial class SimulatorParityTests
{
    // Hosts Kestrel accepts: one it would answer 400 itself never reaches the gate, and SimulatorSocketParityTests pins that rule on real sockets.
    private static readonly string[] RandomHosts =
    [
        "app.test", "APP.TEST", "app.test.", "app.test:8080", "x.app.test", "a.b.app.test", "secure.test", "old.test", "soon.test", "strict.test",
        "h1.test", "noh3.test", "plain.test", "locked.test", "unknown.test", "admin.test", "[::1]", "[::1]:9443", "full.test", "localhost",
        "app.localhost", "127.0.0.1", "pow.test", "open.test", "shadow.test", "limits.test", "robots.test", "custom.test", "bare.test", "upstream.test",
        "lax.test", "v4.test", string.Empty,
    ];

    private static readonly string[] RandomPathPieces = ["/", "api", "x", "health", "blocked", "drop", "protect", "nothing", "robots.txt", ".well-known/acme-challenge/tok-en_1", "..", ".", "%2F", "\\", ";a", "A", " ", "%", "é", "upload", "__paduraksa/pow/verify"];

    private static readonly IPAddress[] RandomPeers =
        [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("198.51.100.1"), IPAddress.Parse("173.245.48.1"), IPAddress.Loopback, IPAddress.Parse("2001:db8::7"), IPAddress.IPv6Loopback];

    private static readonly (bool Https, ProbeHttpVersion Version, SslProtocols Tls)[] RandomConnections =
    [
        (false, ProbeHttpVersion.Http11, SslProtocols.None),
        (true, ProbeHttpVersion.Http11, SslProtocols.Tls12),
        (true, ProbeHttpVersion.Http2, SslProtocols.Tls12),
        (true, ProbeHttpVersion.Http2, SslProtocols.Tls13),
        (true, ProbeHttpVersion.Http3, SslProtocols.Tls13),
    ];

    private static readonly (string Name, string Value)[] RandomHeaders =
    [
        ("Accept", "text/html"), ("Accept", "*/*"), ("User-Agent", "curl/8.5.0"), ("User-Agent", "Mozilla/5.0"), ("X-Forwarded-For", "198.51.100.1"),
        ("X-Forwarded-For", "10.0.0.5, 203.0.113.7"), ("X-Forwarded-For", "bogus"), ("X-Real-IP", "198.51.100.1"), ("CF-Connecting-IP", "198.51.100.1"),
        ("CF-Connecting-IP", "10.0.0.5"), ("CF-Connecting-IP", "nonsense"), ("CF-IPCountry", "DE"), ("CF-IPCountry", "T1"), ("True-Client-IP", "10.0.0.5"),
        ("CF-Visitor", "{\"scheme\":\"https\"}"), ("CF-Visitor", "{\"scheme\":\"http\"}"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-Proto", "http"),
        ("Cookie", "a=b"), ("Cookie", ProofOfWorkToken.CookieName + "=x.y"), ("Content-Length", "0"), ("Content-Length", "11000000"), ("Content-Length", "200000000"),
        ("Transfer-Encoding", "chunked"), ("X-Pad", "ppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppp"),
    ];

    private static readonly Gen<Wire> RandomWires = Gen.Select(
        Property.Pick(RandomHosts),
        Property.Pick(RandomConnections),
        Property.StringOf("/abpxXA.;%\\ ", 0, 5).List[0, 3],
        Gen.Int[0, RandomPathPieces.Length - 1].List[0, 5],
        Property.Pick(RandomPeers),
        Property.Pick("GET", "HEAD", "POST", "DELETE", "OPTIONS"),
        Gen.Int[0, RandomHeaders.Length - 1].List[0, 4],
        Gen.Int[0, 11]).Select(static (host, connection, noise, pieces, peer, method, headers, extra) =>
        {
            var path = "/" + string.Concat(pieces.Select(i => RandomPathPieces[i] + (noise.Count > 0 ? noise[i % noise.Count] : string.Empty)));
            return new Wire(host, connection.Https, connection.Version, connection.Tls, path, peer)
            {
                Method = method,
                Query = extra == 0 ? "a=1&b=%20" : null,
                Headers = [.. headers.Select(i => RandomHeaders[i])],
                ProxyProtocol = extra == 1,
                ServerName = extra == 2 && host.Length > 0 && connection.Https ? "lax.test" : null,

                // Only HTTP/1.1 sends a Host header of its own: HTTP/2 and HTTP/3 carry the authority.
                HostHeaders = connection.Version != ProbeHttpVersion.Http11 ? null : extra switch
                {
                    3 => [],
                    4 => [host, "secure.test"],
                    5 => ["a@b"],
                    _ => null,
                },
            };
        });

    [Fact]
    public async Task TheSimulatorAndTheGateAnswerAnyGeneratedRequestAlike()
    {
        (UnknownHostBehaviour Unknown, TrustedProxySpec Proxies)[] setups =
        [
            (UnknownHostBehaviour.NotFound, TrustedProxySpec.Off),
            (UnknownHostBehaviour.MisdirectedRequest, new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"], TrustForwardedProto = true }),
            (UnknownHostBehaviour.Close, new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy, UseTrueClientIp = true, ForwardedForFallback = true }),
        ];
        var reached = 0;
        HashSet<GateReason> reasons = [];
        foreach (var (unknown, proxies) in setups)
        {
            using Fixture fixture = new(Model(new DefaultsSpec { UnknownHost = unknown, Certificate = TestIds.Certificate("shared"), TrustedProxies = proxies }), _pki);
            RandomWires.Check(
                700,
                wire =>
                {
                    reached += fixture.CompareAsync(wire, $"{unknown} {proxies.Mode}").GetAwaiter().GetResult();
                },
                nameof(TheSimulatorAndTheGateAnswerAnyGeneratedRequestAlike) + unknown);
            reasons.UnionWith(fixture.Reasons);
        }

        Assert.True(reached > 500, $"only {reached} requests reached the gate");
        Assert.True(reasons.Count >= 8, $"the requests only ever got these answers: {string.Join(", ", reasons)}");
    }
}
