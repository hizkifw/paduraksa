using System.Security.Cryptography;
using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The safety net for a refactor of the settings layers (PLAN §5.3): the canonical bytes of a few documents that set
/// <b>every</b> host setting at every layer are pinned by their SHA-256, which is what a stored revision keeps as its hash.
/// A change that alters one byte - key order, a dropped empty record, an enum's spelling - fails here rather than silently
/// rewriting every stored revision.
/// </summary>
public sealed class CanonicalConfigGoldenTests
{
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
                    IpAllowList = new IpAllowListSettings(["10.0.0.0/8", "192.168.1.7"], DropDenied: true),
                    Hsts = new HstsSettings { Enabled = true, MaxAge = 31_536_000, IncludeSubDomains = true, Preload = true },
                    AccessLog = true,
                    RequestIdPropagation = RequestIdPropagation.UpstreamAndResponse,
                    ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Paduraksa },
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
                        IpAllowList = new IpAllowListSettings(["192.168.0.0/16"]),
                        Hsts = new HstsSettings { Enabled = false, MaxAge = 0, IncludeSubDomains = false, Preload = false },
                        AccessLog = false,
                        RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
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
                        IpAllowList = new IpAllowListSettings([]),
                        Hsts = new HstsSettings { MaxAge = 86_400 },
                        AccessLog = true,
                        RequestIdPropagation = RequestIdPropagation.ResponseOnly,
                        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Upstream, Value = "kept but unused" },
                    },
                    Routes:
                    [
                        new RouteConfig(RootRoute, AppUpstream, Order: 10),
                        new RouteConfig(ApiRoute, AppUpstream, Path: "/api", Settings: new RouteSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]) }),
                    ],
                    Tags: ["media"]),
            ]);

        AssertPinned(document, "959A7B0F3AA47A9AD1625FB6159D30FF186EE5152118853FD203DBEAB878AC50");
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
        ConfigDocument emptyLayers = bare with
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
                    },
                },
            ],
        };

        Assert.Equal(CanonicalConfigJson.Serialize(bare), CanonicalConfigJson.Serialize(emptyLayers));
        AssertPinned(bare, "03F89512CDBE4087744DEDB963F71B8D46AB43D337A65D43B7597226206DB370");
    }

    private static void AssertPinned(ConfigDocument document, string expectedSha256)
    {
        byte[] bytes = CanonicalConfigJson.Serialize(document);

        // A round trip through the canonical form is the same document, so a change here can only be a contract change.
        Assert.Equal(bytes, CanonicalConfigJson.Serialize(CanonicalConfigJson.Deserialize(bytes)));

        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(
            actual == expectedSha256,
            $"The canonical bytes of the pinned document changed.{Environment.NewLine}sha256: {actual}{Environment.NewLine}bytes: {Encoding.UTF8.GetString(bytes)}");
    }
}
