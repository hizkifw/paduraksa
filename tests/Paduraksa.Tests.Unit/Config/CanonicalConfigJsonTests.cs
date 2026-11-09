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

        string json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document));

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
            + "\"tags\":[]}]}",
            json);
    }

    [Fact]
    public void TheM6HostSettingsAddNoKeyWhenUnset()
    {
        // A layer that sets neither is byte-for-byte what it was before M6, so a stored revision keeps its hash.
        ConfigDocument untouched = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings(), Routes: [new RouteConfig(RootRoute, AppUpstream)])]);
        string json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(untouched));
        Assert.DoesNotContain("accessLog", json, StringComparison.Ordinal);
        Assert.DoesNotContain("requestIdPropagation", json, StringComparison.Ordinal);

        // Set, they are camelCase keys, and the enum is its camelCase name.
        ConfigDocument set = untouched with
        {
            Hosts = [untouched.Hosts[0] with { Settings = new HostSettings { AccessLog = false, RequestIdPropagation = RequestIdPropagation.ResponseOnly } }],
        };
        string written = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(set));
        Assert.Contains("\"accessLog\":false", written, StringComparison.Ordinal);
        Assert.Contains("\"requestIdPropagation\":\"responseOnly\"", written, StringComparison.Ordinal);

        // And it reads back.
        Assert.Equal(set.Hosts[0].Settings, CanonicalConfigJson.Deserialize(Encoding.UTF8.GetBytes(written)).Hosts[0].Settings);
    }

    [Fact]
    public void NewHostSettingKeysFollowTheRecordsOrder()
    {
        // A new field goes at the end of the record, so the keys a stored document writes keep their order: HSTS, the
        // access log, the request ID's destination, then the Server header.
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
                    },
                    Routes: [new RouteConfig(RootRoute, AppUpstream)]),
            ]);

        string json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document));

        int hsts = json.IndexOf("\"hsts\":", StringComparison.Ordinal);
        int accessLog = json.IndexOf("\"accessLog\":", StringComparison.Ordinal);
        int propagation = json.IndexOf("\"requestIdPropagation\":", StringComparison.Ordinal);
        int serverHeader = json.IndexOf("\"serverHeader\":", StringComparison.Ordinal);
        Assert.True(hsts > 0 && accessLog > hsts && propagation > accessLog && serverHeader > propagation, json);
    }

    [Fact]
    public void RoundTripsByteForByte()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            byte[] bytes = CanonicalConfigJson.Serialize(Random(seed));

            Assert.Equal(bytes, CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(bytes)));
        }
    }

    [Fact]
    public void EquivalentDocumentsSerializeIdentically()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            ConfigDocument document = Random(seed);

            Assert.Equal(CanonicalConfigJson.Serialize(document), CanonicalConfigJson.Serialize(Respelled(document, seed)));
        }
    }

    [Fact]
    public void CanonicalizeIsIdempotent()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            ConfigDocument once = CanonicalConfigJson.Canonicalize(Respelled(Random(seed), seed));
            ConfigDocument twice = CanonicalConfigJson.Canonicalize(once);

            Assert.Equal(Write(once), Write(twice));
        }
    }

    [Fact]
    public void DifferentConfigsSerializeDifferently()
    {
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
        {
            Hosts = [.. document.Hosts.Select(h => h.Id == AppHost ? h with { Enabled = false } : h)],
        };

        Assert.NotEqual(CanonicalConfigJson.Serialize(document), CanonicalConfigJson.Serialize(changed));
    }

    [Fact]
    public void SortsObjectsByIdAndRoutesStablyByOrder()
    {
        static RouteConfig Route(int id, int order) => new(Id<RouteId>(id), AppUpstream, order);
        ConfigDocument document = new(Hosts:
        [
            new HostConfig(Id<HostId>(9), ["b.example.com"]),
            new HostConfig(Id<HostId>(4), ["a.example.com"], Routes: [Route(5, 10), Route(3, 0), Route(1, 10), Route(2, 0)]),
        ]);

        ConfigDocument canonical = CanonicalConfigJson.Canonicalize(document);

        Assert.Equal([Id<HostId>(4), Id<HostId>(9)], canonical.Hosts.Select(h => h.Id));
        Assert.Equal([Id<RouteId>(3), Id<RouteId>(2), Id<RouteId>(5), Id<RouteId>(1)], canonical.Hosts[0].Routes.Select(r => r.Id));
    }

    [Fact]
    public void ListedOrderOfTiedRoutesIsConfig()
    {
        // A newer /api listed before an older catch-all: /api matches first. Swapping them is a different config.
        RouteConfig api = new(Id<RouteId>(2), AppUpstream, Path: "/api");
        RouteConfig root = new(Id<RouteId>(1), AppUpstream);
        static ConfigDocument Document(params RouteConfig[] routes) =>
            new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Routes: routes)]);

        Assert.Equal([api.Id, root.Id], CanonicalConfigJson.Canonicalize(Document(api, root)).Hosts[0].Routes.Select(r => r.Id));
        Assert.NotEqual(CanonicalConfigJson.Serialize(Document(api, root)), CanonicalConfigJson.Serialize(Document(root, api)));
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
            new GlobalSettings(Defaults: new HostSettings { IpAllowList = new IpAllowListSettings([input]) }));

        Assert.Equal([expected], CanonicalConfigJson.Canonicalize(document).Settings.Defaults.IpAllowList!.Addresses);
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

        HostConfig canonical = Assert.Single(CanonicalConfigJson.Canonicalize(document).Hosts);

        Assert.Equal("https://example.com", canonical.Redirect!.Target);
        Assert.Equal(["a.example.com"], canonical.Names);
    }

    [Fact]
    public void WritesUndefinedEnumsAsNumbersThatDontReadBack()
    {
        ConfigDocument document = new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Kind: (HostKind)9)]);

        byte[] bytes = CanonicalConfigJson.Serialize(document);

        Assert.Contains("\"kind\":9", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => CanonicalConfigJson.Deserialize(bytes));
    }

    [Fact]
    public void ANullHstsLayerAddsNoKeyAndHstsChangesTheHash()
    {
        // Revision hashes stay stable for configs that don't use HSTS: no key, not even an empty object.
        ConfigDocument without = new(Hosts: [new HostConfig(AppHost, ["app.example.com"], Settings: new HostSettings { ForceHttps = true })]);
        Assert.DoesNotContain("hsts", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(without)), StringComparison.OrdinalIgnoreCase);

        ConfigDocument emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, Hsts = new HstsSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        // Set, it is written as the numbers and words they are, and it changes the bytes.
        ConfigDocument enabled = without with
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
        string json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(enabled));
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

        ConfigDocument emptyLayer = without with
        {
            Hosts = [without.Hosts[0] with { Settings = new HostSettings { ForceHttps = true, ServerHeader = new ServerHeaderSettings() } }],
        };
        Assert.Equal(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(emptyLayer));

        ConfigDocument custom = without with
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
        string json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(custom));
        Assert.Contains("\"serverHeader\":{\"mode\":\"custom\",\"value\":\"edge/1.0\"}", json, StringComparison.Ordinal);

        Assert.NotEqual(CanonicalConfigJson.Serialize(without), CanonicalConfigJson.Serialize(custom));
        Assert.Equal(CanonicalConfigJson.Serialize(custom), CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(custom))));
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
                    Settings: new HostSettings { Tls = new TlsSettings(), IpAllowList = new IpAllowListSettings(["b", "a", "a"]) },
                    Tags: ["Media", " media", "LAN"],
                    Notes: " \n") { Certificate = null!, Routes = null! },
            ]);

        ConfigDocument canonical = CanonicalConfigJson.Canonicalize(document);

        Assert.Equal(["10.0.0.1", "::1"], canonical.Settings.TrustedProxies.Proxies);
        Assert.Null(canonical.Settings.TrustedProxies.Header);
        HostConfig host = Assert.Single(canonical.Hosts);
        Assert.Equal(["*.example.com", "not a name", "xn--bcher-kva.example"], host.Names);
        Assert.Null(host.Settings.Tls);
        Assert.Equal(["a", "b"], host.Settings.IpAllowList!.Addresses);
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
    [InlineData("""{"settings":{"defaults":{"ipAllowList":{"addresses":[null]}}}}""", "/settings/defaults/ipAllowList/addresses/0")]
    [InlineData(
        """{"hosts":[{"id":"host_0000000000000000000001","names":["a"],"routes":[{"id":"rte_0000000000000000000001","upstreamId":"ups_0000000000000000000001","settings":{"ipAllowList":{"addresses":["::1",null]}}}]}]}""",
        "/hosts/0/routes/0/settings/ipAllowList/addresses/1")]
    [InlineData("null", "")]
    public void RejectsWhatIsNotTheModel(string json, string path)
    {
        // Every case but the null document is a v1 document, so that the only fault is the one under test.
        byte[] utf8 = Encoding.UTF8.GetBytes(json.StartsWith('{') ? "{\"apiVersion\":\"paduraksa.dev/v1\"," + json[1..] : json);

        Assert.ThrowsAny<JsonException>(() => CanonicalConfigJson.Deserialize(utf8));
        Assert.False(CanonicalConfigJson.TryDeserialize(utf8, out ConfigDocument? document, out ConfigError? error));
        Assert.Null(document);
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.StartsWith(path, error.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"apiVersion":"paduraksa.dev/v2","hosts":[{"newField":1}]}""")]
    [InlineData("""{"hosts":[{"x":{"apiVersion":"nested"}}],"waf":{},"apiVersion":"paduraksa.dev/v2"}""")]
    public void ReportsAnotherApiVersionBeforeItsUnknownFields(string json)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);

        Assert.False(CanonicalConfigJson.TryDeserialize(utf8, out _, out ConfigError? error));
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
        Assert.False(CanonicalConfigJson.TryDeserialize(Encoding.UTF8.GetBytes(json), out _, out ConfigError? error));
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.StartsWith(path, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresTheApiVersion()
    {
        Assert.False(CanonicalConfigJson.TryDeserialize("""{"hosts":[]}"""u8, out _, out ConfigError? error));
        Assert.Equal(CanonicalConfigJson.InvalidJsonCode, error.Code);
        Assert.Contains("apiVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAMinimalDocumentWithDefaults()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(
            $$"""{"apiVersion":"paduraksa.dev/v1","hosts":[{"id":"{{AppHost}}","names":["a.example.com"],"routes":[{"id":"{{RootRoute}}","upstreamId":"{{AppUpstream}}"}]}]}""");

        Assert.True(CanonicalConfigJson.TryDeserialize(utf8, out ConfigDocument? document, out _));

        Assert.Equal(ConfigDocument.V1, document.ApiVersion);
        HostConfig host = Assert.Single(document.Hosts);
        Assert.True(host.Enabled);
        Assert.Equal(HostKind.Proxy, host.Kind);
        Assert.Equal(CertificateBindingMode.Auto, host.Certificate.Mode);
        RouteConfig route = Assert.Single(host.Routes);
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
