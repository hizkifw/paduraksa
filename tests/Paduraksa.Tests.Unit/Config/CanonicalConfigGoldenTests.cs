using System.Security.Cryptography;
using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The safety net for a refactor of the settings layers: the canonical bytes of a few documents that set
/// <b>every</b> host setting at every layer are pinned by their SHA-256, which is what a stored revision keeps as its hash.
/// A change that alters one byte - key order, a dropped empty record, an enum's spelling - fails here rather than silently
/// rewriting every stored revision.
/// </summary>
public sealed class CanonicalConfigGoldenTests
{
    // A fixed verifier: a freshly made one has a random salt, and these bytes are pinned.
    private const string Hash = "$pbkdf2-sha256$i=210000$c2FsdHNhbHRzYWx0c2FsdA$YWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWE";

    [Fact]
    public void ARichDocumentKeepsItsBytes()
    {
        ConfigDocument document = new(
            Settings: new GlobalSettings(
                Version: 4,
                Defaults: new HostSettings
                {
                    ForceHttps = true,
                    Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13, TcpAlpn = TcpAlpn.Http11Only, Http3 = false },
                    Hsts = new HstsSettings { Enabled = true, MaxAge = 31_536_000, IncludeSubDomains = true, Preload = true },
                    AccessLog = true,
                    RequestIdPropagation = RequestIdPropagation.UpstreamAndResponse,
                    ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Paduraksa },
                    ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = 16, TokenLifetimeHours = 168 },
                    RateLimit = new RateLimitSettings { Enabled = true, PolicyId = ApiPolicy },
                    AccessList = new AccessListSettings { Enabled = true, AccessListId = LanList },
                },
                UnknownHost: UnknownHostBehaviour.MisdirectedRequest,
                TrustedProxies: new TrustedProxySettings(TrustedProxyMode.CloudflareProxy, [], UseTrueClientIp: true)),
            Profiles:
            [
                new ProfileConfig(
                    LanProfile,
                    "LAN only",
                    Version: 2,
                    Settings: new HostSettings
                    {
                        ForceHttps = false,
                        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = TcpAlpn.Http2AndHttp11, Http3 = true },
                        Hsts = new HstsSettings { Enabled = false, MaxAge = 0, IncludeSubDomains = false, Preload = false },
                        AccessLog = false,
                        RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
                        ProofOfWork = new ProofOfWorkSettings { Enabled = false, Difficulty = 20, TokenLifetimeHours = 24 },
                        AccessList = new AccessListSettings { AccessListId = LanList },
                    },
                    Notes: "the LAN preset"),
            ],
            Upstreams:
            [
                new UpstreamConfig(
                    AppUpstream,
                    "app",
                    Destinations: [new DestinationConfig(AppDestination, "http://app:8080")]),
            ],
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["app.example.com", "*.app.example.com"],
                    Version: 3,
                    ProfileId: LanProfile,
                    Settings: new HostSettings
                    {
                        ForceHttps = true,
                        Tls = new TlsSettings { Http3 = false },
                        Hsts = new HstsSettings { MaxAge = 86_400 },
                        AccessLog = true,
                        RequestIdPropagation = RequestIdPropagation.ResponseOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Upstream, Value = "kept but unused" },
                        ProofOfWork = new ProofOfWorkSettings { Difficulty = 18, TokenLifetimeHours = 720 },
                        RateLimit = new RateLimitSettings { PolicyId = ApiPolicy },
                        AccessList = new AccessListSettings { Enabled = false },
                    },
                    Routes:
                    [
                        new RouteConfig(RootRoute, AppUpstream, Order: 10),
                        new RouteConfig(ApiRoute, AppUpstream, Path: "/api", Settings: new RouteSettings
                        {
                            ProofOfWork = new ProofOfWorkSettings { Enabled = true },
                            RateLimit = new RateLimitSettings { Enabled = false, PolicyId = ApiPolicy },
                            AccessList = new AccessListSettings { Enabled = true, AccessListId = LanList },
                        }),
                    ],
                    Tags: ["media"]),
            ],
            RateLimitPolicies:
            [
                new RateLimitPolicyConfig(ApiPolicy, "API", Version: 1, Requests: 30, Per: RateLimitPeriod.Second, Burst: 60, Key: RateLimitKey.ClientIpAndPath, Notes: "the API budget"),
            ],
            AccessLists:
            [
                new AccessListConfig(
                    LanList,
                    "Office",
                    Version: 2,
                    Notes: "the office network",
                    Ip: new AccessListIp(["192.168.1.7", "10.0.0.0/8"], ["10.9.0.0/16"], DropDenied: true),
                    BasicAuth: new AccessListBasicAuth("Staff", [new AccessListUserConfig("bob", Hash), new AccessListUserConfig("alice", Hash)]),
                    Satisfy: AccessListSatisfy.Any,
                    PassCredentialsUpstream: true),
            ]);

        AssertPinned(document, "F78403DE8538FE6D823824E5472A351C7FADC859121E4C531F8A3318517E609B");
    }

    [Fact]
    public void ALayerThatSetsNothingKeepsTheBytesOfNoLayer()
    {
        // The rule a refactor must not lose: an empty nested record is not part of the document, so a config that never
        // mentions HSTS, TLS or the Server header hashes exactly as it did before those settings existed.
        ConfigDocument bare = new(
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["app.example.com"],
                    Settings: new HostSettings { ForceHttps = true },
                    Routes: [new RouteConfig(RootRoute, AppUpstream)]),
            ]);
        var emptyLayers = bare with
        {
            Hosts =
            [
                bare.Hosts[0] with
                {
                    Settings = bare.Hosts[0].Settings with
                    {
                        Tls = new TlsSettings(),
                        Hsts = new HstsSettings(),
                        ServerHeader = new ServerHeaderSettings(),
                        ProofOfWork = new ProofOfWorkSettings(),
                        RateLimit = new RateLimitSettings(),
                        AccessList = new AccessListSettings(),
                    },
                },
            ],
        };

        Assert.Equal(CanonicalConfigJson.Serialize(bare), CanonicalConfigJson.Serialize(emptyLayers));
        AssertPinned(bare, "A2CF517FF9E8B1D6E7DF241080EFEFD0240B076B1ABF98905B34674F09E71417");
    }

    private static void AssertPinned(ConfigDocument document, string expectedSha256)
    {
        var bytes = CanonicalConfigJson.Serialize(document);

        // A round trip through the canonical form is the same document, so a change here can only be a contract change.
        Assert.Equal(bytes, CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(bytes)));

        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(
            actual == expectedSha256,
            $"The canonical bytes of the pinned document changed.{Environment.NewLine}sha256: {actual}{Environment.NewLine}bytes: {Encoding.UTF8.GetString(bytes)}");
    }
}
