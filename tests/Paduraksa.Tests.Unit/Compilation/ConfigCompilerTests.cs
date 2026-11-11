using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

public sealed class ConfigCompilerTests : IDisposable
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);

    private readonly AnyCertificate _certificates = new();


    public void Dispose() => _certificates.Dispose();

    [Fact]
    public void AValidDocumentCompilesToALabelledCandidate()
    {
        var compilation = _certificates.Compile(Valid(), Revision);

        Assert.True(compilation.Succeeded);
        Assert.Empty(compilation.Errors);
        Assert.Equal(Revision, compilation.Snapshot!.Revision);
        Assert.Equal(0, compilation.Snapshot.Generation);
        Assert.Equal(AppHost, compilation.Snapshot.Hosts.Find("x.app.example.com")!.Id);
    }

    [Fact]
    public void AnAdminHostWithoutTheInternalListenerWarnsAndCompilesAsUnavailable()
    {
        ConfigDocument document = new(Hosts: [new HostConfig(AppHost, ["admin.example.com"], Kind: HostKind.Admin)]);
        ConfigCompiler compiler = new(new SnapshotCompilerOptions { AdminUpstream = false });

        var compilation = compiler.Compile(document, Revision, _certificates.InventoryFor(document));

        Assert.True(compilation.Succeeded);
        var warning = Assert.Single(compilation.Warnings, w => w.Code == ConfigWarningCodes.AdminInternalDisabled);
        Assert.Equal("/hosts/0", warning.Path);
        Assert.Equal(-1, Assert.Single(compilation.Snapshot!.HostsByIndex[0].Routes).Slot);
    }

    [Fact]
    public void AStaleEdgeRangeListWarnsOnlyWhereTheEdgeRangesAreTrusted()
    {
        var late = EdgeRangeList.BuiltIn.FetchedAt.AddDays(45);
        CloudflareEdgeRangeSource source = new(new Clock(late));
        ConfigCompiler compiler = new(new SnapshotCompilerOptions { CloudflareRanges = source });
        static ConfigDocument Mode(TrustedProxyMode mode) => new(
            Settings: new GlobalSettings(TrustedProxies: new TrustedProxySettings(mode, mode == TrustedProxyMode.CloudflareTunnel ? ["127.0.0.1"] : [])),
            Hosts: [new HostConfig(AppHost, ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200))]);

        var proxy = Mode(TrustedProxyMode.CloudflareProxy);
        var stale = compiler.Compile(proxy, Revision, _certificates.InventoryFor(proxy));
        var warning = Assert.Single(stale.Warnings, w => w.Code == ConfigWarningCodes.CloudflareRangesStale);
        Assert.Equal("/settings/trustedProxies", warning.Path);
        Assert.Contains("45 days old", warning.Message, StringComparison.Ordinal);

        foreach (var mode in new[] { TrustedProxyMode.Off, TrustedProxyMode.Generic, TrustedProxyMode.CloudflareTunnel })
        {
            var other = Mode(mode);
            Assert.DoesNotContain(
                compiler.Compile(other, Revision, _certificates.InventoryFor(other)).Warnings,
                w => w.Code == ConfigWarningCodes.CloudflareRangesStale);
        }

        // A fresh list says nothing.
        Assert.True(EdgeRangeList.TryCreate(
            ["173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32"], late, out var fresh, out _));
        source.Replace(fresh);
        Assert.DoesNotContain(
            compiler.Compile(proxy, Revision, _certificates.InventoryFor(proxy)).Warnings,
            w => w.Code == ConfigWarningCodes.CloudflareRangesStale);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void ErrorsPointAtTheObjectInTheDocumentAsGiven()
    {
        // Not in canonical order: hosts and upstreams by descending ID, routes by descending order, a disabled
        // destination first. The compiler reads the canonical form; the paths index what was given.
        var spare = Id<UpstreamId>(2);
        ConfigDocument document = new(
            Settings: new GlobalSettings(DefaultCertificateId: Id<CertificateId>(1)),
            Upstreams:
            [
                new UpstreamConfig(spare, "spare", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://spare")]),
                new UpstreamConfig(
                    AppUpstream,
                    "app",
                    Destinations: [new DestinationConfig(Id<DestinationId>(3), "ftp://off", Enabled: false), new DestinationConfig(AppDestination, "ftp://app")]),
            ],
            Hosts:
            [
                new HostConfig(
                    Id<HostId>(2),
                    ["b.example.com"],
                    Certificate: new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = Id<CertificateId>(9) },
                    Routes: [new RouteConfig(RootRoute, spare, Order: 10), new RouteConfig(ApiRoute, spare, Path: "api")]),
                new HostConfig(AppHost, ["a.example.com"], Routes: [new RouteConfig(Id<RouteId>(3), spare)]),
            ]);

        var compilation = _certificates.Compile(document, Revision);

        Assert.False(compilation.Succeeded);
        Assert.Null(compilation.Snapshot);
        Assert.All(compilation.Errors, e => Assert.Equal(CompileFailed, e.Code));
        Assert.Equal(
            ["/hosts/0/routes/1", "/upstreams/1/destinations/1"],
            compilation.Errors.Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ADuplicateIdPointsAtTheLaterObject()
    {
        var document = Valid();
        HostConfig twin = new(AppHost, ["twin.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200));

        var error = Assert.Single(_certificates.Compile(document with { Hosts = [.. document.Hosts, twin] }, Revision).Errors);
        Assert.Equal(("/hosts/3", "duplicate host ID"), (error.Path, error.Message));

        // Two more copies: two duplicate errors, both at the last copy, reported once.
        var triple = document with { Hosts = [.. document.Hosts, twin, twin with { Names = ["twin2.example.com"] }] };
        error = Assert.Single(_certificates.Compile(triple, Revision).Errors);
        Assert.Equal(("/hosts/4", "duplicate host ID"), (error.Path, error.Message));
    }

    [Fact]
    public void AnInheritedValueIsReportedWhereItIsUsed()
    {
        // The missing list is named by the global defaults: each host whose chain carries it reports it.
        var document = Valid();
        var changed = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { AccessList = new AccessListSettings { AccessListId = Id<AccessListId>(9) } } },
            Profiles = [],
            Hosts = [.. document.Hosts.Where(h => h.Id != RespondHost).Select(h => h with { ProfileId = null })],
        };

        var errors = _certificates.Compile(changed, Revision).Errors;

        Assert.Equal(["/hosts/0", "/hosts/1"], errors.Select(e => e.Path));
        Assert.All(errors, e => Assert.Contains("doesn't exist", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ABadAddressIsReportedAtItsAccessList()
    {
        var document = Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", Ip: new AccessListIp(Allow: ["10.0.0.0/8", "10.0.0.1/8"]))] };

        var error = Assert.Single(_certificates.Compile(document, Revision).Errors);

        Assert.Equal("/accessLists/0", error.Path);
        Assert.Contains("'10.0.0.1/8' isn't an IP address or CIDR", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesTheModelCantCarryFailWithAPath()
    {
        var document = Valid();
        var changed = document with
        {
            Upstreams = [document.Upstreams[0] with { LoadBalancing = (LoadBalancingPolicy)9 }],
            Hosts =
            [
                .. document.Hosts.Select(h => h.Id == AppHost ? h with { Certificate = new CertificateBinding { Mode = (CertificateBindingMode)9 } }
                    : h.Id == RespondHost ? h with { Respond = h.Respond! with { ContentType = null! } }
                    : h),
            ],
        };

        var compilation = _certificates.Compile(changed, Revision);

        Assert.False(compilation.Succeeded);
        Assert.Equal(
            [("/hosts/0", "unknown certificate binding mode 9"), ("/hosts/2", "a respond host needs a content type"), ("/upstreams/0", "unknown load-balancing policy 9")],
            compilation.Errors.Select(e => (e.Path, e.Message)));
    }

    [Fact]
    public void ANullRespondBodyIsTheEmptyBody()
    {
        var document = Valid();
        var changed = document with { Hosts = [.. document.Hosts.Select(h => h.Id == RespondHost ? h with { Respond = h.Respond! with { Body = null! } } : h)] };

        Assert.Empty(ConfigValidator.Validate(changed));
        Assert.Empty(_certificates.Compile(changed, Revision).Errors);
    }

    [Fact]
    public void TrustedProxyErrorsPointAtTheTrustedProxies()
    {
        ConfigDocument document = new(Settings: new GlobalSettings(TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic)));

        Assert.Equal("/settings/trustedProxies", Assert.Single(_certificates.Compile(document, Revision).Errors).Path);
    }

    [Fact]
    public void ForcingHttpsOnAHostWithoutHttpsFailsAtTheHost()
    {
        // The validator reports it too; the compiler is the backstop, and judges the setting after layering.
        var document = Valid();
        var changed = document with
        {
            Hosts = [.. document.Hosts.Select(h => h.Id == RespondHost ? h with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } } : h)],
        };

        var error = Assert.Single(_certificates.Compile(changed, Revision).Errors);
        Assert.Equal(("/hosts/2", CompileFailed), (error.Path, error.Code));
        Assert.Contains("can't force HTTPS", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The backstop property: the validator accepts nothing the compiler rejects (certificates aside, which only the
    /// compiler can resolve; here every ID resolves). A counterexample is a validator gap.
    /// </summary>
    [Fact]
    public void EveryDocumentThatValidatesCompiles()
    {
        foreach (var (name, document) in Samples())
        {
            Assert.True(ConfigValidator.Validate(document).Count == 0, $"{name} should be valid");
            AssertCompiles(name, document);
        }

        var valid = 0;
        var total = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var random = Random(seed);
            foreach (var (name, document) in (ReadOnlySpan<(string, ConfigDocument)>)[($"random {seed}", random), ($"respelled {seed}", Respelled(random, seed))])
            {
                total++;
                if (ConfigValidator.Validate(document).Count == 0)
                {
                    valid++;
                    AssertCompiles(name, document);
                }
            }
        }

        // Not vacuous: a good share of the generated documents are valid.
        Assert.True(valid * 4 >= total, $"only {valid} of {total} documents validate");
    }

    private void AssertCompiles(string name, ConfigDocument document)
    {
        var compilation = _certificates.Compile(document, Revision);
        Assert.True(compilation.Succeeded, $"{name}: {string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
    }

    private static IEnumerable<(string Name, ConfigDocument Document)> Samples()
    {
        yield return ("valid", Valid());
        yield return ("empty", new ConfigDocument());
        yield return ("edges", Edges());
        foreach (var trusted in (TrustedProxySettings[])
            [
                new(TrustedProxyMode.CloudflareProxy, UseTrueClientIp: true, ForwardedForFallback: true),
                new(TrustedProxyMode.CloudflareTunnel, ["127.0.0.1", "::1"]),
                new(TrustedProxyMode.Generic, ["10.0.0.0/8"], Header: " X-Forwarded-For ", TrustForwardedProto: true),
            ])
        {
            yield return ($"trusted proxies {trusted.Mode}", Valid() with { Settings = Valid().Settings with { TrustedProxies = trusted } });
        }
    }

    // Valid spellings at the edges of the rules: IP-literal and IDN names, overlapping exact and wildcard names, paths
    // that differ in case or match kind only, base paths, bodiless statuses, IPv4-mapped blocks, an upstream that only a
    // disabled host routes to with every destination disabled.
    private static ConfigDocument Edges()
    {
        var parked = Id<UpstreamId>(2);
        return new ConfigDocument(
            Settings: new GlobalSettings(Defaults: new HostSettings { AccessList = new AccessListSettings { AccessListId = LanList } }),
            AccessLists: [new AccessListConfig(LanList, "lan", Ip: new AccessListIp([" ::FFFF:10.0.0.0/104 ", "2001:DB8::/32"], DropDenied: true))],
            Upstreams:
            [
                new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "https://app:8443/base/")], ActiveHealth: new ActiveHealthConfig("/")),
                new UpstreamConfig(parked, "parked", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://parked", Enabled: false)]),
            ],
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["[2001:DB8::1]", "192.0.2.10", "Bücher.example", "*.Example.org", " a.example.org. "],
                    Routes:
                    [
                        new RouteConfig(RootRoute, AppUpstream, Order: -5, Match: RouteMatchKind.Exact, Path: "/API"),
                        new RouteConfig(ApiRoute, AppUpstream, Order: -5, Path: "/api", Settings: new RouteSettings { AccessList = new AccessListSettings { Enabled = false } }),
                        new RouteConfig(Id<RouteId>(3), AppUpstream, Order: int.MaxValue, Path: "/a b/é"),
                    ]),
                new HostConfig(Id<HostId>(2), ["parked.example.com"], Enabled: false, Routes: [new RouteConfig(Id<RouteId>(4), parked)]),
                new HostConfig(Id<HostId>(3), ["gone.example.com"], Kind: HostKind.Redirect, Redirect: new RedirectConfig("https://new.example.com/base/", 307)),
                new HostConfig(Id<HostId>(4), ["empty.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(204, ContentType: "text/plain;\tcharset=utf-8")),
                new HostConfig(
                    Id<HostId>(5),
                    ["plain.example.com"],
                    Kind: HostKind.Respond,
                    Certificate: new CertificateBinding { Mode = CertificateBindingMode.None },
                    Respond: new RespondConfig(200)),
            ]);
    }
}
