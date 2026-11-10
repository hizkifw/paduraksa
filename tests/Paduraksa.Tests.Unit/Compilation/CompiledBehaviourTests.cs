using System.Net;

using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

using SystemIpNetwork = System.Net.IPNetwork;

namespace Paduraksa.Tests.Unit.Compilation;

public sealed class CompiledBehaviourTests : IDisposable
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly IPAddress Outsider = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress InA = IPAddress.Parse("10.1.2.3");
    private static readonly IPAddress InB = IPAddress.Parse("192.168.1.9");

    private static readonly IPAddress[] Clients =
        [InA, InB, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::5"), IPAddress.IPv6Loopback, Outsider];

    private static readonly string[] Paths = ["/", "/p1", "/P2/x", "/p3", "/zz"];

    private readonly AnyCertificate _certificates = new();


    public void Dispose() => _certificates.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClientNoRouteAdmitsIsBlockedEverywhere(bool drop)
    {
        // Route A admits 10/8, route B 192.168.1/24; the host forces HTTPS. The outsider gets the same answer on every
        // path, over plain HTTP too (no redirect that would show the host serves it), and nothing tells the paths apart.
        using var gate = Harness(SplitHost(drop));
        var blocked = drop ? GateHarness.Aborted : 403;

        foreach (var scheme in (string[])["http", "https"])
        {
            foreach (var path in (string[])["/", "/a", "/a/x", "/b", "/nothing-here"])
            {
                Assert.Equal(blocked, await gate.SendAsync("app.example.com", scheme, path, Outsider));
            }
        }
    }

    [Fact]
    public async Task AClientInOneRoutesListGetsThroughThereOnly()
    {
        using var gate = Harness(SplitHost(drop: false));

        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/a/x", InA));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/b", InA));
        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/b", InB));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", InB));

        // Admitted by some route: the redirect and the 404 are theirs to see.
        Assert.Equal(301, await gate.SendAsync("app.example.com", "http", "/b", InA));
        Assert.Equal(404, await gate.SendAsync("app.example.com", "https", "/nothing-here", InA));
    }

    [Fact]
    public async Task ALiftedRouteIsOpenToEveryone()
    {
        var document = SplitHost(drop: false);
        var host = document.Hosts[0];
        document = document with { Hosts = [host with { Routes = [host.Routes[0], host.Routes[1] with { Settings = new RouteSettings { IpAllowList = new IpAllowListSettings([]) } }] }] };
        using var gate = Harness(document);

        Assert.Equal(GateHarness.Forwarded, await gate.SendAsync("app.example.com", "https", "/b", Outsider));
        Assert.Equal(403, await gate.SendAsync("app.example.com", "https", "/a", Outsider));
    }

    /// <summary>
    /// The semantic property: for generated documents that validate, the gate answers every probe the way the
    /// reference does.
    /// </summary>
    [Fact]
    public async Task TheGateDoesWhatTheDocumentSays()
    {
        var checkedDocuments = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            // The client is the socket peer: trusted proxies are their own tests.
            var random = Random(seed);
            var document = random with { Settings = random.Settings with { TrustedProxies = new TrustedProxySettings() } };
            if (ConfigValidator.Validate(document).Count > 0)
            {
                continue;
            }

            checkedDocuments++;
            using var gate = Harness(document);
            foreach (var host in document.Hosts.Where(h => h.Enabled))
            {
                foreach (var scheme in (string[])["http", "https"])
                {
                    foreach (var path in Paths)
                    {
                        foreach (var client in Clients)
                        {
                            var expected = Reference.Answer(document, host, scheme, path, client);
                            var actual = await gate.SendAsync(host.Names[0], scheme, path, client);
                            Assert.True(expected == actual, $"seed {seed}, {scheme}://{host.Names[0]}{path} from {client}: expected {expected}, got {actual}");
                        }
                    }
                }
            }
        }

        Assert.True(checkedDocuments >= 50, $"only {checkedDocuments} documents validate");
    }

    private GateHarness Harness(ConfigDocument document)
    {
        var compilation = _certificates.Compile(document, Revision);
        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
        return new GateHarness(compilation.Snapshot!);
    }

    // A proxy host forcing HTTPS, with route "/a" admitting 10/8 and route "/b" admitting 192.168.1/24.
    private static ConfigDocument SplitHost(bool drop) => new(
        Settings: new GlobalSettings(Defaults: new HostSettings { ForceHttps = true }),
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts:
        [
            new HostConfig(
                AppHost,
                ["app.example.com"],
                Routes:
                [
                    new RouteConfig(RootRoute, AppUpstream, Path: "/a", Settings: new RouteSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"], drop) }),
                    new RouteConfig(ApiRoute, AppUpstream, Path: "/b", Settings: new RouteSettings { IpAllowList = new IpAllowListSettings(["192.168.1.0/24"], drop) }),
                ]),
        ]);

    /// <summary>
    /// The intended behaviour, read straight off the document: TLS checks, then a host-wide block for clients no route
    /// (or, without routes, the host) admits, the HTTPS redirect, fixed answers, the first matching route in evaluation
    /// order and its list.
    /// </summary>
    private static class Reference
    {
        public static int Answer(ConfigDocument document, HostConfig host, string scheme, string path, IPAddress client)
        {
            var defaults = document.Settings.Defaults;
            var profile = document.Profiles.FirstOrDefault(p => p.Id == host.ProfileId)?.Settings;
            var https = scheme == "https";
            var minimum = host.Settings.Tls?.MinimumVersion ?? profile?.Tls?.MinimumVersion ?? defaults.Tls?.MinimumVersion ?? TlsMinimumVersion.Tls12;
            if (https && (host.Certificate.Mode == CertificateBindingMode.None || minimum == TlsMinimumVersion.Tls13))
            {
                return 421; // no HTTPS, or a TLS 1.3 minimum this (unknown-version) connection doesn't show
            }

            var hostList = Restricting(host.Settings.IpAllowList ?? profile?.IpAllowList ?? defaults.IpAllowList);
            RouteConfig[] routes =
            [
                .. host.Routes
                    .Select((route, listed) => (Route: route, Listed: listed))
                    .OrderBy(r => r.Route.Match == RouteMatchKind.Exact ? 0 : 1)
                    .ThenByDescending(r => r.Route.Match == RouteMatchKind.Exact ? 0 : r.Route.Path.Length)
                    .ThenBy(r => r.Route.Order)
                    .ThenBy(r => r.Listed)
                    .Select(r => r.Route),
            ];
            IpAllowListSettings?[] routeLists = [.. routes.Select(r => r.Settings.IpAllowList is { } own ? Restricting(own) : hostList)];

            (bool Restricts, IEnumerable<string> Addresses, bool Drop) guard = routes.Length == 0
                ? (hostList is not null, hostList?.Addresses ?? [], hostList?.DropDenied ?? false)
                : (routeLists.All(l => l is not null), routeLists.SelectMany(l => l?.Addresses ?? []), routeLists.All(l => l?.DropDenied ?? false));
            if (guard.Restricts && !Admits(guard.Addresses, client))
            {
                return guard.Drop ? GateHarness.Aborted : 403;
            }

            if (!https && (host.Settings.ForceHttps ?? profile?.ForceHttps ?? defaults.ForceHttps ?? false))
            {
                return 301;
            }

            switch (host.Kind)
            {
                case HostKind.Redirect:
                    return host.Redirect!.StatusCode;
                case HostKind.Respond:
                    return host.Respond!.StatusCode;
            }

            var match = Array.FindIndex(routes, r => Matches(r, path));
            if (match < 0)
            {
                return 404;
            }

            if (routeLists[match] is { } list && !Admits(list.Addresses, client))
            {
                return list.DropDenied ? GateHarness.Aborted : 403;
            }

            return GateHarness.Forwarded;
        }

        private static IpAllowListSettings? Restricting(IpAllowListSettings? list) => list is { Addresses.Count: > 0 } ? list : null;

        private static bool Admits(IEnumerable<string> addresses, IPAddress client) =>
            addresses.Select(a => a.Trim()).Any(a => a.Contains('/', StringComparison.Ordinal)
                ? SystemIpNetwork.Parse(a).Contains(client)
                : IPAddress.Parse(a).Equals(client));

        // Segment-aware, ignoring case.
        private static bool Matches(RouteConfig route, string path) => route.Match == RouteMatchKind.Exact
            ? string.Equals(route.Path, path, StringComparison.OrdinalIgnoreCase)
            : route.Path == "/"
                || string.Equals(route.Path, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(route.Path + "/", StringComparison.OrdinalIgnoreCase);
    }
}
