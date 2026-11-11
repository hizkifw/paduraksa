using System.Text;
using System.Text.Json;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

public sealed class CanonicalConfigJsonTests
{
    private const int Seeds = 300;

    [Fact]
    public void WritesTheCanonicalShape()
    {
        ConfigDocument document = new(
            Upstreams:
            [
                new UpstreamConfig(
                    AppUpstream,
                    "app",
                    Destinations: [new DestinationConfig(AppDestination, "http://app:8080")],
                    PassiveHealth: new PassiveHealthConfig { ReactivationPeriod = TimeSpan.FromSeconds(90) }),
            ],
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["App.Example.COM."],
                    Settings: new HostSettings { Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 } },
                    Routes: [new RouteConfig(RootRoute, AppUpstream)],
                    Notes: "<b>"),
            ]);

        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document));

        Assert.Equal(
            "{\"apiVersion\":\"paduraksa.dev/v1\","
            + "\"settings\":{\"version\":0,\"unknownHost\":\"notFound\",\"defaults\":{},"
            + "\"trustedProxies\":{\"mode\":\"off\",\"useTrueClientIp\":false,\"forwardedForFallback\":false,\"trustForwardedProto\":false,\"proxies\":[]}},"
            + "\"profiles\":[],"
            + $"\"upstreams\":[{{\"id\":\"{AppUpstream}\",\"name\":\"app\",\"version\":0,"
            + "\"sessionAffinity\":false,\"passiveHealth\":{\"reactivationPeriod\":\"00:01:30\"},"
            + $"\"destinations\":[{{\"id\":\"{AppDestination}\",\"address\":\"http://app:8080\",\"enabled\":true}}]}}],"
            + $"\"hosts\":[{{\"id\":\"{AppHost}\",\"names\":[\"app.example.com\"],\"version\":0,\"kind\":\"proxy\",\"enabled\":true,"
            + "\"notes\":\"\\u003Cb\\u003E\",\"certificate\":{\"mode\":\"auto\"},\"settings\":{\"tls\":{\"minimumVersion\":\"tls13\"}},"
            + $"\"routes\":[{{\"id\":\"{RootRoute}\",\"upstreamId\":\"{AppUpstream}\",\"order\":0,\"match\":\"prefix\",\"path\":\"/\",\"settings\":{{}}}}],"
            + "\"tags\":[]}],"
            + "\"rateLimitPolicies\":[],\"accessLists\":[]}",
            json);
    }

    [Fact]
    public void TheM6HostSettingsAddNoKeyWhenUnset()
    {
        // A layer that sets neither is byte-for-byte what it was before M6, so a stored revision keeps its hash.
        ConfigDocument untouched = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings(), Routes: [new RouteConfig(RootRoute, AppUpstream)])]);
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(untouched));
        Assert.DoesNotContain("accessLog", json, StringComparison.Ordinal);
        Assert.DoesNotContain("requestIdPropagation", json, StringComparison.Ordinal);

        // Set, they are camelCase keys, and the enum is its camelCase name.
        var set = untouched with
        {
            Hosts = [untouched.Hosts[0] with { Settings = new HostSettings { AccessLog = false, RequestIdPropagation = RequestIdPropagation.ResponseOnly } }],
        };
        var written = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(set));
        Assert.Contains("\"accessLog\":false", written, StringComparison.Ordinal);
        Assert.Contains("\"requestIdPropagation\":\"responseOnly\"", written, StringComparison.Ordinal);

        // And it reads back.
        Assert.Equal(set.Hosts[0].Settings, CanonicalConfigJson.Deserialize(Encoding.UTF8.GetBytes(written)).Hosts[0].Settings);
    }

    [Fact]
    public void NewHostSettingKeysFollowTheRecordsOrder()
    {
        // A new field goes at the end of the record, so the keys a stored document writes keep their order: HSTS, the
        // access log, the request ID's destination, the Server header, then proof of work.
        ConfigDocument document = new(
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["app.example.com"],
                    Settings: new HostSettings
                    {
                        Hsts = new HstsSettings { Enabled = true },
                        AccessLog = false,
                        RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Remove },
                        ProofOfWork = new ProofOfWorkSettings { Enabled = true },
                    },
                    Routes: [new RouteConfig(RootRoute, AppUpstream)]),
            ]);

        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document));

        var hsts = json.IndexOf("\"hsts\":", StringComparison.Ordinal);
        var accessLog = json.IndexOf("\"accessLog\":", StringComparison.Ordinal);
        var propagation = json.IndexOf("\"requestIdPropagation\":", StringComparison.Ordinal);
        var serverHeader = json.IndexOf("\"serverHeader\":", StringComparison.Ordinal);
        var proofOfWork = json.IndexOf("\"proofOfWork\":", StringComparison.Ordinal);
        Assert.True(hsts > 0 && accessLog > hsts && propagation > accessLog && serverHeader > propagation && proofOfWork > serverHeader, json);
    }

    [Fact]
    public void RoundTripsByteForByte()
    {
        for (var seed = 0; seed < Seeds; seed++)
        {
            var bytes = CanonicalConfigJson.Serialize(Random(seed));

            Assert.Equal(bytes, CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(bytes)));
        }
    }

    [Fact]
    public void EquivalentDocumentsSerializeIdentically()
    {
        for (var seed = 0; seed < Seeds; seed++)
        {
            var document = Random(seed);

            Assert.Equal(CanonicalConfigJson.Serialize(document), CanonicalConfigJson.Serialize(Respelled(document, seed)));
        }
    }

    [Fact]
    public void CanonicalizeIsIdempotent()
    {
        for (var seed = 0; seed < Seeds; seed++)
        {
            var once = CanonicalConfigJson.Canonicalize(Respelled(Random(seed), seed));
            var twice = CanonicalConfigJson.Canonicalize(once);

            Assert.Equal(Write(once), Write(twice));
        }
    }

    [Fact]
    public void DifferentConfigsSerializeDifferently()
    {
        var document = Valid();
        var changed = document with
        {
            Hosts = [.. document.Hosts.Select(h => h.Id == AppHost ? h with { Enabled = false } : h)],
        };

        Assert.NotEqual(CanonicalConfigJson.Serialize(document), CanonicalConfigJson.Serialize(changed));
    }

    [Fact]
    public void SortsObjectsByIdAndRoutesByEvaluationOrder()
    {
        static RouteConfig Route(int id, int order, string path = "/", RouteMatchKind match = RouteMatchKind.Prefix) =>
            new(Id<RouteId>(id), AppUpstream, order, match, path);
        ConfigDocument document = new(Hosts:
        [
            new HostConfig(Id<HostId>(9), ["b.example.com"]),
            new HostConfig(
                Id<HostId>(4),
                ["a.example.com"],
                Routes:
                [
                    Route(5, 10),                              // /, Order 10
                    Route(3, 10, "/api/v1"),                    // the longest prefix
                    Route(1, 10, "/api", RouteMatchKind.Exact), // exact first, whatever its length
                    Route(2, 0, "/a"),                          // /a before / at equal Order
                    Route(4, 0),                                // /, Order 0, before route 5
                ]),
        ]);

        var canonical = CanonicalConfigJson.Canonicalize(document);

        Assert.Equal([Id<HostId>(4), Id<HostId>(9)], canonical.Hosts.Select(h => h.Id));
        Assert.Equal([Id<RouteId>(1), Id<RouteId>(3), Id<RouteId>(2), Id<RouteId>(4), Id<RouteId>(5)], canonical.Hosts[0].Routes.Select(r => r.Id));
    }

    [Fact]
    public void RoutesOfDifferentSpecificityOrderBySpecificityNotByListing()
    {
        // A newer /api listed before an older catch-all, or the other way round: both are evaluated api first.
        RouteConfig api = new(Id<RouteId>(2), AppUpstream, Path: "/api");
        RouteConfig root = new(Id<RouteId>(1), AppUpstream);
        static ConfigDocument Document(params RouteConfig[] routes) =>
            new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Routes: routes)]);

        Assert.Equal([api.Id, root.Id], CanonicalConfigJson.Canonicalize(Document(api, root)).Hosts[0].Routes.Select(r => r.Id));
        Assert.Equal([api.Id, root.Id], CanonicalConfigJson.Canonicalize(Document(root, api)).Hosts[0].Routes.Select(r => r.Id));
        Assert.Equal(CanonicalConfigJson.Serialize(Document(api, root)), CanonicalConfigJson.Serialize(Document(root, api)));
    }

    [Fact]
    public void ListedOrderOfEqualSpecificityIsConfig()
    {
        // Same kind, path length and Order: nothing else distinguishes them, so the listed order is kept and counts.
        RouteConfig a = new(Id<RouteId>(1), AppUpstream, Path: "/a");
        RouteConfig b = new(Id<RouteId>(2), AppUpstream, Path: "/b");
        static ConfigDocument Document(params RouteConfig[] routes) =>
            new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Routes: routes)]);

        Assert.Equal([a.Id, b.Id], CanonicalConfigJson.Canonicalize(Document(a, b)).Hosts[0].Routes.Select(r => r.Id));
        Assert.Equal([b.Id, a.Id], CanonicalConfigJson.Canonicalize(Document(b, a)).Hosts[0].Routes.Select(r => r.Id));
        Assert.NotEqual(CanonicalConfigJson.Serialize(Document(a, b)), CanonicalConfigJson.Serialize(Document(b, a)));
    }

    [Theory]
    [InlineData("2001:DB8::/32", "2001:db8::/32")]
    [InlineData("2001:db8:0:0::1/128", "2001:db8::1")]
    [InlineData("10.0.0.1/32", "10.0.0.1")]
    [InlineData(" 10.0.0.0/8 ", "10.0.0.0/8")]
    [InlineData("::ffff:10.0.0.0/104", "10.0.0.0/8")]
    [InlineData(" not an address ", "not an address")]
    [InlineData("10.0.0.1/8", "10.0.0.1/8")] // host bits set: invalid, kept for validation to report
    public void SpellsAddressesTheWayTheParserPrintsThem(string input, string expected)
    {
        ConfigDocument document = new(
            ConfigDocument.V1,
            AccessLists: [new AccessListConfig(Id<AccessListId>(1), "list", Ip: new AccessListIp([input], [input]))]);

        var ip = CanonicalConfigJson.Canonicalize(document).AccessLists[0].Ip!;
        Assert.Equal([expected], ip.Allow);
        Assert.Equal([expected], ip.Deny);
    }

    [Fact]
    public void WritesCountriesUpperCaseSortedAndOnce()
    {
        ConfigDocument document = new(
            ConfigDocument.V1,
            AccessLists: [new AccessListConfig(Id<AccessListId>(1), "list", Ip: new AccessListIp(Countries: new AccessListCountries([" nl", "DE", "de", "t1"], ["xx", "RU"])))]);

        var canonical = CanonicalConfigJson.Canonicalize(document).AccessLists[0].Ip!.Countries!;
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document));

        Assert.Equal(["DE", "NL", "T1"], canonical.Allow);
        Assert.Equal(["RU", "XX"], canonical.Deny);
        Assert.Contains("\"countries\":{\"allow\":[\"DE\",\"NL\",\"T1\"],\"deny\":[\"RU\",\"XX\"]}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountryListThatSaysNothingIsNotPartOfTheDocument()
    {
        AccessListConfig Make(AccessListCountries? countries, bool drop = false) =>
            new(Id<AccessListId>(1), "list", Ip: new AccessListIp(DropDenied: drop, Countries: countries));
        string Json(AccessListConfig list) => Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(new ConfigDocument(ConfigDocument.V1, AccessLists: [list])));

        Assert.Null(CanonicalConfigJson.Canonicalize(new ConfigDocument(ConfigDocument.V1, AccessLists: [Make(new AccessListCountries([], []))])).AccessLists[0].Ip);
        Assert.DoesNotContain("countries", Json(Make(new AccessListCountries([], []), drop: true)), StringComparison.Ordinal);
        Assert.DoesNotContain("countries", Json(Make(null, drop: true)), StringComparison.Ordinal);
        Assert.Equal(Json(Make(null)), Json(Make(new AccessListCountries(null, null))));
    }

    [Fact]
    public void CountriesRoundTripThroughTheirOwnBytes()
    {
        ConfigDocument document = new(
            ConfigDocument.V1,
            AccessLists: [new AccessListConfig(Id<AccessListId>(1), "list", Ip: new AccessListIp(["10.0.0.0/8"], Countries: new AccessListCountries(["DE"], ["T1", "XX"])))]);

        var bytes = CanonicalConfigJson.Serialize(document);
        var read = CanonicalConfigJson.Serialize(CanonicalConfigJson.Canonicalize(document));

        Assert.Equal(bytes, read);
    }

    [Theory]
    [InlineData(" X-Real-IP ", "x-real-ip")]
    [InlineData("X-Forwarded-For", null)]
    [InlineData(" ", null)]
    public void LowercasesTheForwardingHeaderAndDropsTheDefault(string header, string? expected)
    {
        ConfigDocument document = new(
            ConfigDocument.V1,
            new GlobalSettings(TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["10.0.0.1"], header)));

        Assert.Equal(expected, CanonicalConfigJson.Canonicalize(document).Settings.TrustedProxies.Header);
    }

    [Fact]
    public void TrimsRedirectTargetsAndDropsNullElements()
    {
        HostConfig host = new(AppHost, ["a.example.com", null!], Kind: HostKind.Redirect, Redirect: new RedirectConfig(" https://example.com "));
        ConfigDocument document = new(ConfigDocument.V1, Hosts: [host, null!]);

        var canonical = Assert.Single(CanonicalConfigJson.Canonicalize(document).Hosts);

        Assert.Equal("https://example.com", canonical.Redirect!.Target);
        Assert.Equal(["a.example.com"], canonical.Names);
    }

    [Fact]
    public void WritesUndefinedEnumsAsNumbersThatDontReadBack()
    {
        ConfigDocument document = new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Kind: (HostKind)9)]);

        var bytes = CanonicalConfigJson.Serialize(document);

        Assert.Contains("\"kind\":9", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => CanonicalConfigJson.Deserialize(bytes));
    }

    [Fact]
    public void ANullHstsLayerAddsNoKeyAndHstsChangesTheHash()
    {
        // Revision hashes stay stable for configs that don't use HSTS: no key, not even an empty object.
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("hsts", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        var emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, Hsts = new HstsSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        // Set, it is written as the numbers and words they are, and it changes the bytes.
        var enabled = without with
        {
            Hosts =
            [
                without.Hosts[0] with
                {
                    Settings = new HostSettings
                    {
                        ForceHttps = true,
                        Hsts = new HstsSettings { Enabled = true, MaxAge = 31_536_000, IncludeSubDomains = true, Preload = false },
                    },
                },
            ],
        };
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(enabled));
        Assert.Contains("\"hsts\":{\"enabled\":true,\"maxAge\":31536000,\"includeSubDomains\":true,\"preload\":false}", json, StringComparison.Ordinal);
        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(enabled));
        Assert.Equal(CanonicalConfigJson.Serialize(enabled), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(enabled))));
    }

    [Fact]
    public void ANullServerHeaderLayerAddsNoKeyAndItsValueChangesTheHash()
    {
        // Existing revisions keep their hash: a config that doesn't set the Server header writes no key at all.
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("serverheader", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        var emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, ServerHeader = new ServerHeaderSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        var custom = without with
        {
            Hosts =
            [
                without.Hosts[0] with
                {
                    Settings = new HostSettings
                    {
                        ForceHttps = true,
                        AccessLog = false,
                        RequestIdPropagation = RequestIdPropagation.ResponseOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
                    },
                },
            ],
        };
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(custom));
        Assert.Contains("\"serverHeader\":{\"mode\":\"custom\",\"value\":\"edge/1.0\"}", json, StringComparison.Ordinal);

        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(custom));
        Assert.Equal(CanonicalConfigJson.Serialize(custom), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(custom))));
    }

    [Fact]
    public void ANullProofOfWorkLayerAddsNoKeyAndItsValuesChangeTheHash()
    {
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("proofofwork", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        var emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, ProofOfWork = new ProofOfWorkSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        var enabled = without with
        {
            Hosts =
            [
                without.Hosts[0] with
                {
                    Settings = new HostSettings { ForceHttps = true, ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = 20, TokenLifetimeHours = 24 } },
                    Routes =
                    [
                        new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = false } }),
                    ],
                },
            ],
        };
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(enabled));
        Assert.Contains("\"proofOfWork\":{\"enabled\":true,\"difficulty\":20,\"tokenLifetimeHours\":24}", json, StringComparison.Ordinal);
        Assert.Contains("\"proofOfWork\":{\"enabled\":false}", json, StringComparison.Ordinal);
        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(enabled));
        Assert.Equal(CanonicalConfigJson.Serialize(enabled), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(enabled))));
    }

    [Fact]
    public void ANullRobotsLayerAddsNoKeyAndItsValuesChangeTheHash()
    {
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("robots", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        var emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, Robots = new RobotsSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        var custom = without with
        {
            Hosts =
            [
                without.Hosts[0] with
                {
                    Settings = new HostSettings
                    {
                        ForceHttps = true,
                        Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "User-agent: *\r\nDisallow: /\r\n" },
                    },
                },
            ],
        };
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(custom));

        // The stored document keeps the text as written; only the proxy normalises line endings when it compiles.
        Assert.Contains("\"robots\":{\"mode\":\"custom\",\"customText\":\"User-agent: *\\r\\nDisallow: /\\r\\n\"}", json, StringComparison.Ordinal);
        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(custom));
        Assert.Equal(CanonicalConfigJson.Serialize(custom), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(custom))));
    }

    [Fact]
    public void ANullCloudflareLayerAddsNoKeyAndItsValuesChangeTheHash()
    {
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("cloudflare", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        var emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, Cloudflare = new CloudflareSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        var locked = without with
        {
            Hosts =
            [
                without.Hosts[0] with
                {
                    Settings = new HostSettings
                    {
                        ForceHttps = true,
                        Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Drop, AuthenticatedOriginPulls = true },
                    },
                },
            ],
        };
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(locked));

        Assert.Contains("\"cloudflare\":{\"originLockdown\":\"drop\",\"authenticatedOriginPulls\":true}", json, StringComparison.Ordinal);
        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(locked));
        Assert.Equal(CanonicalConfigJson.Serialize(locked), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(locked))));
    }

    [Fact]
    public void NormalisesSetsAndBlankValues()
    {
        ConfigDocument document = new(
            Settings: new GlobalSettings(TrustedProxies: new TrustedProxySettings(Proxies: [" 10.0.0.1 ", "10.0.0.1/32", "::1"], Header: " ")),
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["B\u00fccher.Example.", "*.EXAMPLE.com", "b\u00fccher.example", "not a name "],
                    Settings: new HostSettings { Tls = new TlsSettings() },
                    Tags: ["Media", " media", "LAN"],
                    Notes: " \n") { Certificate = null!, Routes = null! },
            ]);

        var canonical = CanonicalConfigJson.Canonicalize(document);

        Assert.Equal(["10.0.0.1", "::1"], canonical.Settings.TrustedProxies.Proxies);
        Assert.Null(canonical.Settings.TrustedProxies.Header);
        var host = Assert.Single(canonical.Hosts);
        Assert.Equal(["*.example.com", "not a name", "xn--bcher-kva.example"], host.Names);
        Assert.Null(host.Settings.Tls);
        Assert.Equal(["lan", "media"], host.Tags);
        Assert.Null(host.Notes);
        Assert.Equal(new CertificateBinding(), host.Certificate);
        Assert.Empty(host.Routes);
    }

    [Theory]
    [InlineData("""{"hostz":[]}""", "/hostz")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a.example.com"],"kind":"proxy","colour":"red"}]}""", "/hosts/0/colour")]
    [InlineData("""{"hosts":[],"hosts":[]}""", "/hosts")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":null}]}""", "/hosts/0/names")]
    [InlineData("""{"hosts":[{"names":["a.example.com"]}]}""", "/hosts/0")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":1}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":"static"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"rte_0000000000000000000001","names":["a"]}]}""", "/hosts/0/id")]
    [InlineData("""{"Hosts":[]}""", "/Hosts")]
    [InlineData("""{"hosts":[],}""", "")]
    [InlineData("""{"upstreams":[{"id":"ups_0000000000000000000001","name":"a","activeHealth":{"interval":15}}]}""", "/upstreams/0/activeHealth/interval")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":"Proxy"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":"PROXY"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":"proxy,redirect"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":"1"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"kind":" proxy"}]}""", "/hosts/0/kind")]
    [InlineData("""{"hosts":[null]}""", "/hosts/0")]
    [InlineData("""{"profiles":[null]}""", "/profiles/0")]
    [InlineData("""{"upstreams":[{"id":"ups_0000000000000000000001","name":"a","destinations":[null]}]}""", "/upstreams/0/destinations/0")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a",null]}]}""", "/hosts/0/names/1")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"tags":[null]}]}""", "/hosts/0/tags/0")]
    [InlineData("""{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"routes":[null]}]}""", "/hosts/0/routes/0")]
    [InlineData("""{"settings":{"trustedProxies":{"proxies":[null]}}}""", "/settings/trustedProxies/proxies/0")]
    [InlineData("""{"accessLists":[{"id":"acl_0000000000000000000001","name":"a","ip":{"allow":[null]}}]}""", "/accessLists/0/ip/allow/0")]
    [InlineData("""{"accessLists":[{"id":"acl_0000000000000000000001","name":"a","ip":{"allow":["::1"],"deny":["::1",null]}}]}""", "/accessLists/0/ip/deny/1")]
    [InlineData("""{"accessLists":[{"id":"acl_0000000000000000000001","name":"a","basicAuth":{"users":[null]}}]}""", "/accessLists/0/basicAuth/users/0")]
    [InlineData("null", "")]
    public void RejectsWhatIsNotTheModel(string json, string path)
    {
        // Every case but the null document is a v1 document, so that the only fault is the one under test.
        var utf8 = Encoding.UTF8.GetBytes(json.StartsWith('{') ? "{\"apiVersion\":\"paduraksa.dev/v1\"," + json[1..] : json);

        Assert.ThrowsAny<JsonException>(() => CanonicalConfigJson.Deserialize(utf8));
        Assert.False(CanonicalConfigJson.TryDeserialize(utf8, out var document, out var error));
        Assert.Null(document);
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.StartsWith(path, error.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"apiVersion":"paduraksa.dev/v2","hosts":[{"newField":1}]}""")]
    [InlineData("""{"hosts":[{"x":{"apiVersion":"nested"}}],"waf":{},"apiVersion":"paduraksa.dev/v2"}""")]
    public void ReportsAnotherApiVersionBeforeItsUnknownFields(string json)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);

        Assert.False(CanonicalConfigJson.TryDeserialize(utf8, out _, out var error));
        Assert.Equal(("/apiVersion", ConfigErrorCodes.ApiVersionUnsupported), (error.Path, error.Code));
        Assert.Contains("paduraksa.dev/v2", error.Message, StringComparison.Ordinal);
        Assert.Equal("$.apiVersion", Assert.ThrowsAny<JsonException>(() => CanonicalConfigJson.Deserialize(utf8)).Path);
    }

    [Theory]
    [InlineData("""{"apiVersion":2,"hosts":[]}""", "/apiVersion")]
    [InlineData("""{"apiVersion":"paduraksa.dev/v1","hostz":[]}""", "/hostz")]
    [InlineData("""{"apiVersion":"paduraksa.dev/v2""", "")]
    public void LeavesOtherFaultsToTheReader(string json, string path)
    {
        Assert.False(CanonicalConfigJson.TryDeserialize(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.StartsWith(path, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresTheApiVersion()
    {
        Assert.False(CanonicalConfigJson.TryDeserialize("""{"hosts":[]}"""u8, out _, out var error));
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.Contains("apiVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAMinimalDocumentWithDefaults()
    {
        var utf8 = Encoding.UTF8.GetBytes(
            $$"""{"apiVersion":"paduraksa.dev/v1","hosts":[{"id":"{{AppHost}}","names":["a.example.com"],"routes":[{"id":"{{RootRoute}}","upstreamId":"{{AppUpstream}}"}]}]}""");

        Assert.True(CanonicalConfigJson.TryDeserialize(utf8, out var document, out _));

        Assert.Equal(ConfigDocument.V1, document.ApiVersion);
        var host = Assert.Single(document.Hosts);
        Assert.True(host.Enabled);
        Assert.Equal(HostKind.Proxy, host.Kind);
        Assert.Equal(CertificateBindingMode.Auto, host.Certificate.Mode);
        var route = Assert.Single(host.Routes);
        Assert.Equal(("/", RouteMatchKind.Prefix), (route.Path, route.Match));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("$", "")]
    [InlineData("$.hosts", "/hosts")]
    [InlineData("$.hosts[3].names[0]", "/hosts/3/names/0")]
    [InlineData("$['odd.name'].x", "/odd.name/x")]
    [InlineData("$['a/b~c']", "/a~1b~0c")]
    public void ConvertsJsonPathsToPointers(string? jsonPath, string expected) =>
        Assert.Equal(expected, CanonicalConfigJson.ToJsonPointer(jsonPath));

    private static byte[] Write(ConfigDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, ConfigJsonContext.Default.ConfigDocument);
}
