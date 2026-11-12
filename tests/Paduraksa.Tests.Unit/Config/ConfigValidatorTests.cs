using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

using HstsHeader = Paduraksa.Proxy.Tls.HstsHeader;

namespace Paduraksa.Tests.Unit.Config;

public sealed class ConfigValidatorTests
{
    private static readonly System.Buffers.SearchValues<char> Upper = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

    // Valid() as written: profile 0, upstream 0, hosts 0 (proxy, routes 0 = "/", 1 = "/api"), 1 (redirect), 2 (respond).
    // Paths index the input document, which is validated as sent.
    private const string Proxy = "/hosts/0";
    private const string RedirectHostPath = "/hosts/1";
    private const string RespondHostPath = "/hosts/2";

    [Fact]
    public void AcceptsTheSamples()
    {
        Assert.Empty(ConfigValidator.Validate(Valid()));
        Assert.Empty(ConfigValidator.Validate(CanonicalConfigJson.Canonicalize(Valid())));
        Assert.Empty(ConfigValidator.Validate(new ConfigDocument()));
    }

    [Fact]
    public void PathsIndexTheDocumentAsSent()
    {
        // Hosts and their names listed out of canonical order: the error points at what the client wrote.
        var document = Valid();
        var unsorted = document with
        {
            Hosts =
            [
                .. document.Hosts.Reverse().Select(h => h.Id == RespondHost ? h with { Names = ["zz.example.com", "-bad-.example.com"] } : h),
            ],
        };

        AssertErrors(unsorted, ("/hosts/0/names/1", HostNameInvalid));
        Assert.Equal("/hosts/2/names/0", ConfigValidator.Validate(CanonicalConfigJson.Canonicalize(unsorted)).Single().Path);
    }

    [Fact]
    public void RejectsOtherApiVersions() =>
        AssertErrors(Valid() with { ApiVersion = "paduraksa.dev/v2" }, ("/apiVersion", ApiVersionUnsupported));

    [Fact]
    public void RejectsEmptyAndDuplicateIds()
    {
        var document = Valid();
        var changed = document with
        {
            Profiles = [.. document.Profiles, new ProfileConfig(LanProfile, "other")],
            Upstreams =
            [
                .. document.Upstreams,
                new UpstreamConfig(default, "empty", Destinations: [new DestinationConfig(AppDestination, "http://b")]),
            ],
            Hosts = MapHost(document, AppHost, h => h with
            {
                Routes = [.. h.Routes, new RouteConfig(ApiRoute, AppUpstream, Order: 99, Path: "/x")],
            }),
        };

        AssertErrors(
            changed,
            ("/profiles/1/id", IdDuplicate),
            ("/upstreams/1/id", IdEmpty),
            ("/upstreams/1/destinations/0/id", IdDuplicate),
            ($"{Proxy}/routes/2/id", IdDuplicate));
    }

    [Fact]
    public void IdsDifferingOnlyInCaseAreDuplicates()
    {
        // YARP compares route, cluster and destination IDs ignoring case.
        var text = AppUpstream.ToString();
        var letter = text.AsSpan(UpstreamId.Prefix.Length + 1).LastIndexOfAny(Upper) + UpstreamId.Prefix.Length + 1;
        Assert.True(letter > UpstreamId.Prefix.Length, "the sample ID needs an upper-case letter");
        var twin = UpstreamId.Parse(string.Concat(text.AsSpan(0, letter), char.ToLowerInvariant(text[letter]).ToString(), text.AsSpan(letter + 1)), null);
        Assert.NotEqual(AppUpstream, twin);

        var document = Valid();
        var changed = document with
        {
            Upstreams = [.. document.Upstreams, new UpstreamConfig(twin, "twin", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://b")])],
        };

        AssertErrors(changed, ("/upstreams/1/id", IdDuplicate));
    }

    [Fact]
    public void RouteIdsAreUniqueAcrossHosts()
    {
        var document = Valid();
        HostConfig other = new(
            Id<HostId>(9),
            ["other.example.com"],
            Routes: [new RouteConfig(RootRoute, AppUpstream)]);

        AssertErrors(document with { Hosts = [.. document.Hosts, other] }, ("/hosts/3/routes/0/id", IdDuplicate));
    }

    [Fact]
    public void RejectsNegativeVersions()
    {
        var document = Valid();
        var changed = document with
        {
            Settings = document.Settings with { Version = -1 },
            Hosts = MapHost(document, RespondHost, h => h with { Version = -3 }),
        };

        AssertErrors(changed, ("/settings/version", VersionNegative), ($"{RespondHostPath}/version", VersionNegative));
    }

    [Fact]
    public void RejectsUndefinedEnums()
    {
        var document = Valid();
        var changed = document with
        {
            Settings = document.Settings with
            {
                UnknownHost = (UnknownHostBehaviour)9,
                Defaults = new HostSettings { Tls = new TlsSettings { MinimumVersion = (TlsMinimumVersion)9, TcpAlpn = (TcpAlpn)9 } },
                TrustedProxies = new TrustedProxySettings((TrustedProxyMode)9),
            },
            Upstreams = [document.Upstreams[0] with { LoadBalancing = (LoadBalancingPolicy)9 }],
            Hosts = MapHost(document, AppHost, h => h with
            {
                Certificate = new CertificateBinding { Mode = (CertificateBindingMode)9 },
                Routes = [h.Routes[0] with { Match = (RouteMatchKind)9 }],
            }),
        };
        var kind = document with { Hosts = MapHost(document, RespondHost, h => h with { Kind = (HostKind)9 }) };

        AssertErrors(
            changed,
            ("/settings/defaults/tls/minimumVersion", EnumUndefined),
            ("/settings/defaults/tls/tcpAlpn", EnumUndefined),
            ("/settings/unknownHost", EnumUndefined),
            ("/settings/trustedProxies/mode", EnumUndefined),
            ("/upstreams/0/loadBalancing", EnumUndefined),
            ($"{Proxy}/certificate/mode", EnumUndefined),
            ($"{Proxy}/routes/0/match", EnumUndefined));
        AssertErrors(kind, ($"{RespondHostPath}/kind", EnumUndefined));
    }

    [Fact]
    public void ProfileAndUpstreamNamesAreRequiredShortAndUniqueIgnoringCase()
    {
        var document = Valid();
        var changed = document with
        {
            Profiles =
            [
                .. document.Profiles,
                new ProfileConfig(Id<ProfileId>(2), " lan ONLY "),
                new ProfileConfig(Id<ProfileId>(3), "  "),
                new ProfileConfig(Id<ProfileId>(4), new string('p', 101)),
            ],
            Upstreams =
            [
                .. document.Upstreams,
                new UpstreamConfig(Id<UpstreamId>(2), "APP", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://b")]),
            ],
        };

        AssertErrors(
            changed,
            ("/profiles/1/name", NameDuplicate),
            ("/profiles/2/name", NameRequired),
            ("/profiles/3/name", NameTooLong),
            ("/upstreams/1/name", NameDuplicate));
    }

    [Fact]
    public void ChecksUpstreams()
    {
        var document = Valid();
        var upstream = document.Upstreams[0] with
        {
            Destinations =
            [
                new DestinationConfig(Id<DestinationId>(2), "app:8080"),
                new DestinationConfig(Id<DestinationId>(3), "ftp://app"),
                new DestinationConfig(Id<DestinationId>(4), "https://user:secret@app"),
                new DestinationConfig(Id<DestinationId>(5), " https://app:8443/base "),
                new DestinationConfig(Id<DestinationId>(6), "http://app/?x=1"),
                new DestinationConfig(Id<DestinationId>(7), "http://app/#top"),
            ],
            MaxConnectionsPerServer = 0,
            PassiveHealth = new PassiveHealthConfig { ReactivationPeriod = TimeSpan.Zero },
            ActiveHealth = new ActiveHealthConfig("health?x", TimeSpan.FromSeconds(-1), TimeSpan.Zero),
        };
        UpstreamConfig empty = new(Id<UpstreamId>(2), "empty");

        AssertErrors(
            document with { Upstreams = [upstream, empty] },
            ("/upstreams/0/destinations/0/address", DestinationAddressInvalid),
            ("/upstreams/0/destinations/1/address", DestinationAddressInvalid),
            ("/upstreams/0/destinations/2/address", DestinationAddressCredentials),
            ("/upstreams/0/destinations/4/address", DestinationAddressQueryOrFragment),
            ("/upstreams/0/destinations/5/address", DestinationAddressQueryOrFragment),
            ("/upstreams/0/maxConnectionsPerServer", UpstreamMaxConnectionsInvalid),
            ("/upstreams/0/passiveHealth/reactivationPeriod", UpstreamReactivationPeriodInvalid),
            ("/upstreams/0/activeHealth/path", UpstreamActiveHealthPathInvalid),
            ("/upstreams/0/activeHealth/interval", UpstreamActiveHealthIntervalInvalid),
            ("/upstreams/0/activeHealth/timeout", UpstreamActiveHealthTimeoutInvalid),
            ("/upstreams/1/destinations", UpstreamDestinationsRequired));
    }

    [Fact]
    public void UpstreamTlsTrustNeedsPinsExactlyWhenItPins()
    {
        var document = Valid();
        var upstream = document.Upstreams[0];
        string pin = new('a', 64);

        AssertErrors(
            document with
            {
                Upstreams =
                [
                    upstream with { Tls = new UpstreamTlsConfig(UpstreamTlsTrust.Pinned) },
                    upstream with { Id = Id<UpstreamId>(2), Name = "b", Destinations = [new DestinationConfig(Id<DestinationId>(92), "https://b")], Tls = new UpstreamTlsConfig(UpstreamTlsTrust.System, [pin]) },
                    upstream with { Id = Id<UpstreamId>(3), Name = "c", Destinations = [new DestinationConfig(Id<DestinationId>(93), "https://c")], Tls = new UpstreamTlsConfig(UpstreamTlsTrust.Pinned, [pin, "not-a-fingerprint"]) },
                    upstream with { Id = Id<UpstreamId>(4), Name = "d", Destinations = [new DestinationConfig(Id<DestinationId>(94), "https://d")], Tls = new UpstreamTlsConfig((UpstreamTlsTrust)42) },
                ],
            },
            ("/upstreams/0/tls/pinnedSha256", UpstreamTlsPinsMissing),
            ("/upstreams/1/tls/pinnedSha256", UpstreamTlsPinsUnused),
            ("/upstreams/2/tls/pinnedSha256/1", UpstreamTlsPinInvalid),
            ("/upstreams/3/tls/trust", EnumUndefined));
    }

    [Fact]
    public void AnUpstreamAnEnabledHostRoutesToNeedsAnEnabledDestination()
    {
        var document = Valid();
        var off = document.Upstreams[0] with
        {
            Destinations = [.. document.Upstreams[0].Destinations.Select(d => d with { Enabled = false })],
        };
        UpstreamConfig unused = new(Id<UpstreamId>(2), "unused", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://b", Enabled: false)]);

        AssertErrors(document with { Upstreams = [off, unused] }, ("/upstreams/0/destinations", UpstreamDestinationsNoneEnabled));

        // Only disabled hosts route to it: fine, like an unused upstream.
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Upstreams = [off, unused],
            Hosts = MapHost(document, AppHost, h => h with { Enabled = false }),
        }));
    }

    [Fact]
    public void ChecksHostNames()
    {
        var document = Valid();
        var changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with { Names = ["app.example.com", "*.com", "a..b", "APP.example.com."] })
                .Append(new HostConfig(Id<HostId>(9), [], Kind: HostKind.Respond, Respond: new RespondConfig(200)))
                .ToArray(),
        };

        AssertErrors(
            changed,
            ($"{Proxy}/names/1", HostNameInvalid),
            ($"{Proxy}/names/2", HostNameInvalid),
            ($"{Proxy}/names/3", HostNameDuplicate),
            ("/hosts/3/names", HostNamesRequired));
    }

    [Fact]
    public void HostNamesAreUniqueAcrossEnabledHostsOnly()
    {
        var document = Valid();
        static HostConfig Copy(int id, bool enabled, string name) =>
            new(Id<HostId>(id), [name], Kind: HostKind.Redirect, Enabled: enabled, Redirect: new RedirectConfig("https://example.com"));

        // A disabled host may repeat an enabled host's name (a staged replacement), and so may two disabled hosts.
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Hosts = [.. document.Hosts, Copy(7, false, "app.example.com"), Copy(8, false, "app.example.com")],
        }));

        AssertErrors(
            document with { Hosts = [.. document.Hosts, Copy(7, true, "Old.Example.com")] },
            ("/hosts/3/names/0", HostNameDuplicate));
    }

    [Fact]
    public void ChecksReferences()
    {
        var document = Valid();
        var changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with
            {
                ProfileId = Id<ProfileId>(9),
                Routes = [h.Routes[0] with { UpstreamId = Id<UpstreamId>(9) }],
            }),
        };

        AssertErrors(changed, ($"{Proxy}/profileId", HostProfileUnknown), ($"{Proxy}/routes/0/upstreamId", RouteUpstreamUnknown));
    }

    [Fact]
    public void AnEmptyReferenceIsMissing_NotUnknown()
    {
        // A form that left a picker empty sends the empty ID: that is a choice not made, not an object that doesn't exist.
        var document = Valid();
        var changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with
            {
                ProfileId = default(ProfileId),
                Routes = [h.Routes[0] with { UpstreamId = default }],
            }),
        };

        AssertErrors(changed, ($"{Proxy}/profileId", IdEmpty), ($"{Proxy}/routes/0/upstreamId", RouteUpstreamRequired));
    }

    [Fact]
    public void ChecksCertificateBindings()
    {
        var document = Valid();
        var changed = document with
        {
            Settings = document.Settings with { DefaultCertificateId = default(CertificateId) },
            Hosts = MapHost(
                MapHost(document, AppHost, h => h with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific } }),
                RedirectHost,
                h => h with
                {
                    Certificate = new CertificateBinding { Mode = CertificateBindingMode.None, CertificateId = Id<CertificateId>(1) },
                    Settings = new HostSettings { ForceHttps = false },
                }),
        };

        AssertErrors(
            changed,
            ("/settings/defaultCertificateId", IdEmpty),
            ($"{Proxy}/certificate/certificateId", HostCertificateIdRequired),
            ($"{RedirectHostPath}/certificate/certificateId", HostCertificateIdUnexpected));
    }

    [Fact]
    public void AHostWithoutHttpsCantForceHttpsAfterLayering()
    {
        // Valid() forces HTTPS in the global defaults; the app host's profile is the LAN profile.
        var document = Valid();
        CertificateBinding none = new() { Mode = CertificateBindingMode.None };
        var inherited = document with { Hosts = MapHost(document, RespondHost, h => h with { Certificate = none }) };
        var fromProfile = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings() },
            Profiles = [document.Profiles[0] with { Settings = new HostSettings { ForceHttps = true } }],
            Hosts = MapHost(document, AppHost, h => h with { Certificate = none }),
        };
        var own = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings() },
            Hosts = MapHost(document, RespondHost, h => h with { Certificate = none, Settings = new HostSettings { ForceHttps = true } }),
        };
        var lifted = document with
        {
            Hosts = MapHost(document, RespondHost, h => h with { Certificate = none, Settings = new HostSettings { ForceHttps = false } }),
        };

        AssertErrors(inherited, ($"{RespondHostPath}/certificate/mode", HostCertificateNoneWithForceHttps));
        Assert.Contains("inherited from the global defaults", ConfigValidator.Validate(inherited).Single().Message, StringComparison.Ordinal);
        AssertErrors(fromProfile, ($"{Proxy}/certificate/mode", HostCertificateNoneWithForceHttps));
        Assert.Contains("inherited from its profile", ConfigValidator.Validate(fromProfile).Single().Message, StringComparison.Ordinal);
        AssertErrors(own, ($"{RespondHostPath}/certificate/mode", HostCertificateNoneWithForceHttps));
        AssertErrors(lifted);
    }

    [Fact]
    public void ChecksKindSpecificParts()
    {
        var document = Valid();
        RedirectConfig redirect = new("https://example.com");
        RespondConfig respond = new(200);
        RouteConfig route = new(Id<RouteId>(9), AppUpstream);

        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [], Redirect = redirect, Respond = respond }) },
            ($"{Proxy}/routes", HostRoutesRequired),
            ($"{Proxy}/redirect", HostRedirectUnexpected),
            ($"{Proxy}/respond", HostRespondUnexpected));
        AssertErrors(
            document with { Hosts = MapHost(document, RedirectHost, h => h with { Routes = [route], Redirect = null, Respond = respond }) },
            ($"{RedirectHostPath}/routes", HostRoutesUnexpected),
            ($"{RedirectHostPath}/respond", HostRespondUnexpected),
            ($"{RedirectHostPath}/redirect", HostRedirectRequired));
        AssertErrors(
            document with { Hosts = MapHost(document, RespondHost, h => h with { Routes = [route], Redirect = redirect, Respond = null }) },
            ($"{RespondHostPath}/routes", HostRoutesUnexpected),
            ($"{RespondHostPath}/redirect", HostRedirectUnexpected),
            ($"{RespondHostPath}/respond", HostRespondRequired));
    }

    [Fact]
    public void AdminHostsAreSingleAndHaveNoRoutesOrOtherAnswer()
    {
        var document = Valid();
        HostConfig admin = new(Id<HostId>(8), ["admin.example.com"], Kind: HostKind.Admin);
        RouteConfig route = new(Id<RouteId>(9), AppUpstream);

        // One admin host is valid (Auto binding, HTTPS implied by the mapping).
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = [.. document.Hosts, admin] }));

        AssertErrors(
            document with { Hosts = [.. document.Hosts, admin with { Routes = [route], Redirect = new RedirectConfig("https://x.test"), Respond = new RespondConfig(200) }] },
            ("/hosts/3/routes", HostRoutesUnexpected),
            ("/hosts/3/redirect", HostRedirectUnexpected),
            ("/hosts/3/respond", HostRespondUnexpected));

        // At most one, enabled or not.
        AssertErrors(
            document with { Hosts = [.. document.Hosts, admin, admin with { Id = Id<HostId>(9), Names = ["admin2.example.com"], Enabled = false }] },
            ("/hosts/4/kind", HostAdminDuplicate));

        // HTTPS is required: mode none can't serve an admin host.
        AssertErrors(
            document with { Hosts = [.. document.Hosts, admin with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } }] },
            ("/hosts/3/certificate/mode", HostAdminHttpsRequired));
    }

    [Theory]
    [InlineData("example.com", 301, true, "/target", RedirectTargetInvalid)]
    [InlineData("mailto:a@example.com", 301, true, "/target", RedirectTargetInvalid)]
    [InlineData("https://a:b@example.com", 301, true, "/target", RedirectTargetCredentials)]
    [InlineData("https://example.com/?q=1", 301, true, "/target", RedirectTargetQueryWithPreservePath)]
    [InlineData("https://example.com/#top", 301, true, "/target", RedirectTargetQueryWithPreservePath)]
    [InlineData("https://example.com", 200, true, "/statusCode", RedirectStatusCodeInvalid)]
    [InlineData("https://example.com", 304, false, "/statusCode", RedirectStatusCodeInvalid)]
    [InlineData("https://exa mple.com", 301, true, "/target", RedirectTargetInvalid)]
    [InlineData("https://example.com/\r\nSet-Cookie: x=1", 301, true, "/target", RedirectTargetInvalid)]
    [InlineData("https://example.com/a\tb", 301, true, "/target", RedirectTargetInvalid)]
    [InlineData("https://b\u00fccher.example", 301, true, "/target", RedirectTargetInvalid)]
    public void ChecksRedirects(string target, int status, bool preservePath, string field, string code)
    {
        var document = Valid();

        AssertErrors(
            document with { Hosts = MapHost(document, RedirectHost, h => h with { Redirect = new RedirectConfig(target, status, preservePath) }) },
            ($"{RedirectHostPath}/redirect{field}", code));
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Hosts = MapHost(document, RedirectHost, h => h with { Redirect = new RedirectConfig(" https://example.com/?q=1 ", 308, PreservePath: false) }),
        }));
    }

    [Theory]
    [InlineData(199, "", "text/plain", "/statusCode", RespondStatusCodeInvalid)]
    [InlineData(600, "", "text/plain", "/statusCode", RespondStatusCodeInvalid)]
    [InlineData(204, "x", "text/plain", "/body", RespondBodyForbidden)]
    [InlineData(304, "x", "text/plain", "/body", RespondBodyForbidden)]
    [InlineData(200, "", " ", "/contentType", RespondContentTypeInvalid)]
    [InlineData(200, "", "text/plain\r\nX-Evil: 1", "/contentType", RespondContentTypeInvalid)]
    [InlineData(200, "", "text/plain; charset=\u00fc", "/contentType", RespondContentTypeInvalid)]
    [InlineData(200, "", "text/plain\u0000", "/contentType", RespondContentTypeInvalid)]
    public void ChecksFixedResponses(int status, string body, string contentType, string field, string code)
    {
        var document = Valid();
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Hosts = MapHost(document, RespondHost, h => h with { Respond = new RespondConfig(200, "<p>ok</p>", "text/html;\tcharset=utf-8") }),
        }));

        AssertErrors(
            document with { Hosts = MapHost(document, RespondHost, h => h with { Respond = new RespondConfig(status, body, contentType) }) },
            ($"{RespondHostPath}/respond{field}", code));
    }

    [Theory]
    [InlineData("api", RoutePathInvalid)]
    [InlineData("", RoutePathInvalid)]
    [InlineData("/api?x=1", RoutePathInvalid)]
    [InlineData("/api#x", RoutePathInvalid)]
    [InlineData("/a%2Fb", RoutePathEncoded)]
    [InlineData("/a//b", RoutePathNotCanonical)]
    [InlineData("/a/../b", RoutePathNotCanonical)]
    [InlineData("/a\\b", RoutePathNotCanonical)]
    [InlineData("/a;x", RoutePathNotCanonical)]
    [InlineData("/admin.", RoutePathNotCanonical)]
    public void ChecksRoutePaths(string path, string code)
    {
        var document = Valid();
        var changed = document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [h.Routes[0] with { Path = path }] }) };

        AssertErrors(changed, ($"{Proxy}/routes/0/path", code));
    }

    [Fact]
    public void RejectsRoutesThatCanNeverMatch()
    {
        var document = Valid();
        static RouteConfig Route(int id, string path, RouteMatchKind match = RouteMatchKind.Prefix) => new(Id<RouteId>(id), AppUpstream, Path: path, Match: match);
        var changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with
            {
                Routes = [Route(10, "/API"), Route(11, "/api"), Route(12, "/api", RouteMatchKind.Exact), Route(13, "/Api", RouteMatchKind.Exact)],
            }),
        };

        AssertErrors(changed, ($"{Proxy}/routes/1/path", RoutePathDuplicate), ($"{Proxy}/routes/3/path", RoutePathDuplicate));

        // Same path in different hosts is fine.
        HostConfig other = new(Id<HostId>(9), ["other.example.com"], Routes: [Route(14, "/api")]);
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = [.. document.Hosts, other] }));
    }

    [Fact]
    public void AcceptsCanonicalRoutePaths()
    {
        var document = Valid();
        foreach (var path in new[] { "/", "/api", "/api/", "/a/b.c/d", "/ÄPI" })
        {
            Assert.Empty(ConfigValidator.Validate(document with
            {
                Hosts = MapHost(document, AppHost, h => h with { Routes = [h.Routes[0] with { Path = path }] }),
            }));
        }
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off, new[] { "10.0.0.1" }, null, false, false, false, "/proxies", TrustedProxiesProxiesUnexpected)]
    [InlineData(TrustedProxyMode.CloudflareProxy, new[] { "10.0.0.1" }, null, false, false, false, "/proxies", TrustedProxiesProxiesUnexpected)]
    [InlineData(TrustedProxyMode.Generic, new string[0], null, false, false, false, "/proxies", TrustedProxiesProxiesRequired)]
    [InlineData(TrustedProxyMode.CloudflareTunnel, new string[0], null, false, false, false, "/proxies", TrustedProxiesProxiesRequired)]
    [InlineData(TrustedProxyMode.Generic, new[] { "10.0.0.0/40" }, null, false, false, false, "/proxies/0", AddressInvalid)]
    [InlineData(TrustedProxyMode.Generic, new[] { "10.0.0.1" }, "X Real IP", false, false, false, "/header", TrustedProxiesHeaderInvalid)]
    [InlineData(TrustedProxyMode.Generic, new[] { "10.0.0.1/8" }, null, false, false, false, "/proxies/0", AddressInvalid)]
    [InlineData(TrustedProxyMode.CloudflareTunnel, new[] { "10.0.0.1" }, "X-Real-IP", false, false, false, "/header", TrustedProxiesOptionUnexpected)]
    [InlineData(TrustedProxyMode.Generic, new[] { "10.0.0.1" }, null, true, false, false, "/useTrueClientIp", TrustedProxiesOptionUnexpected)]
    [InlineData(TrustedProxyMode.Off, new string[0], null, false, true, false, "/forwardedForFallback", TrustedProxiesOptionUnexpected)]
    [InlineData(TrustedProxyMode.CloudflareProxy, new string[0], null, false, false, true, "/trustForwardedProto", TrustedProxiesOptionUnexpected)]
    public void ChecksTrustedProxies(
        TrustedProxyMode mode,
        string[] proxies,
        string? header,
        bool trueClientIp,
        bool forwardedForFallback,
        bool forwardedProto,
        string field,
        string code)
    {
        var document = Valid();
        TrustedProxySettings trusted = new(mode, proxies, header, trueClientIp, forwardedForFallback, forwardedProto);

        AssertErrors(document with { Settings = document.Settings with { TrustedProxies = trusted } }, ($"/settings/trustedProxies{field}", code));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(256, false)]
    [InlineData(100_000, false)]
    [InlineData(100_001, true)]
    public void ChecksThePerIpConnectionLimit(int limit, bool refused)
    {
        var document = Valid();
        var changed = document with { Settings = document.Settings with { Connections = new ConnectionSettings(MaxConnectionsPerIp: limit) } };

        if (refused)
        {
            AssertErrors(changed, ("/settings/connections/maxConnectionsPerIp", ConnectionsMaxPerIpInvalid));
        }
        else
        {
            Assert.Empty(ConfigValidator.Validate(changed));
        }
    }

    [Fact]
    public void TheConnectionDenyListMustBeAnAccessListThatExists()
    {
        var document = Valid();
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Connections = new ConnectionSettings(DenyListId: LanList) } }));

        var missing = Id<AccessListId>(99);
        AssertErrors(
            document with { Settings = document.Settings with { Connections = new ConnectionSettings(DenyListId: missing) } },
            ("/settings/connections/denyListId", ConnectionsDenyListUnknown));
        AssertErrors(
            document with { Settings = document.Settings with { Connections = new ConnectionSettings(DenyListId: default(AccessListId)) } },
            ("/settings/connections/denyListId", IdEmpty));

        // Deleting the list the settings name is refused like deleting one a host names.
        AssertErrors(
            document with
            {
                Settings = document.Settings with { Connections = new ConnectionSettings(DenyListId: LanList) },
                AccessLists = [],
                Profiles = [.. document.Profiles.Select(p => p with { Settings = new HostSettings() })],
            },
            ("/settings/connections/denyListId", ConnectionsDenyListUnknown));
    }

    [Fact]
    public void ChecksTags()
    {
        var document = Valid();
        var changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with { Tags = [" Media ", "k8s:prod/eu", "", "two words", new string('t', 65)] }),
        };

        AssertErrors(changed, ($"{Proxy}/tags/2", HostTagInvalid), ($"{Proxy}/tags/3", HostTagInvalid), ($"{Proxy}/tags/4", HostTagInvalid));
    }

    [Fact]
    public void ChecksLengthsAndControlCharacters()
    {
        var document = Valid();
        string notes = new('n', ConfigValidator.MaxNotesLength + 1);
        var changed = document with
        {
            Profiles = [document.Profiles[0] with { Name = "LAN\u0007only", Notes = notes }],
            Upstreams = [document.Upstreams[0] with { Notes = notes }],
            Hosts = MapHost(
                MapHost(document, AppHost, h => h with { Notes = notes }),
                RespondHost,
                h => h with { Respond = new RespondConfig(200, new string('b', ConfigValidator.MaxBodyLength + 1), new string('t', ConfigValidator.MaxContentTypeLength + 1)) }),
        };

        AssertErrors(
            changed,
            ("/profiles/0/name", NameInvalid),
            ("/profiles/0/notes", NotesTooLong),
            ("/upstreams/0/notes", NotesTooLong),
            ($"{Proxy}/notes", NotesTooLong),
            ($"{RespondHostPath}/respond/body", RespondBodyTooLong),
            ($"{RespondHostPath}/respond/contentType", RespondContentTypeInvalid));
    }

    [Fact]
    public void ReportsNullElementsOfHandBuiltDocuments()
    {
        var document = Valid();
        var changed = document with
        {
            Profiles = [null!],
            Upstreams = [document.Upstreams[0] with { Destinations = [.. document.Upstreams[0].Destinations, null!] }],
            Hosts = [.. MapHost(document, AppHost, h => h with { Routes = [.. h.Routes, null!], ProfileId = null }), null!],
        };

        AssertErrors(
            changed,
            ("/profiles/0", ValueNull),
            ("/upstreams/0/destinations/1", ValueNull),
            ($"{Proxy}/routes/2", ValueNull),
            ("/hosts/3", ValueNull));
    }

    [Fact]
    public void ReportsEveryErrorWithAMessage()
    {
        var document = Valid();
        var broken = document with
        {
            ApiVersion = "v0",
            Upstreams = [],
            Hosts = MapHost(document, AppHost, h => h with { Names = ["-bad-"] }),
        };

        var errors = ConfigValidator.Validate(broken);

        Assert.Equal(4, errors.Count); // apiVersion, the name, two routes to a missing upstream
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        Assert.Contains(errors, e => e.Message.Contains("-bad-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAdminListenersCertificateCannotBeBound()
    {
        // It is reserved for the admin port: the process's own certificate is not the config's to serve.
        var document = Valid();
        var specific = document with
        {
            Hosts = MapHost(document, AppHost, host => host with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = AdminCertificate.Id } }),
        };
        AssertErrors(specific, ($"{Proxy}/certificate/certificateId", HostCertificateUnusable));

        var asDefault = document with { Settings = document.Settings with { DefaultCertificateId = AdminCertificate.Id } };
        AssertErrors(asDefault, ("/settings/defaultCertificateId", SettingsDefaultCertificateUnusable));
    }

    [Fact]
    public void ChecksTheServerHeaderValueInEveryLayerAndCustomNeedsOne()
    {
        var document = Valid();
        HostSettings invalid = new() { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "bad\tvalue" } };
        HostSettings tooLong = new() { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = new string('a', 129) } };

        AssertErrors(
            document with { Settings = document.Settings with { Defaults = invalid } },
            ("/settings/defaults/serverHeader/value", ServerHeaderValueInvalid));
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = tooLong }] },
            ("/profiles/0/settings/serverHeader/value", ServerHeaderValueInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = invalid }) },
            ($"{Proxy}/settings/serverHeader/value", ServerHeaderValueInvalid));

        // Custom with no value is refused where it would be sent, like HSTS's preload pair.
        HostSettings custom = new() { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } };
        var missing = Assert.Single(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = custom }) }));
        Assert.Equal(($"{Proxy}/settings/serverHeader/value", ServerHeaderValueRequired), (missing.Path, missing.Code));
        HostSettings empty = new() { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = string.Empty } };
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = empty }) },
            ($"{Proxy}/settings/serverHeader/value", ServerHeaderValueRequired));

        // The mode and the value may come from different layers: field by field, the resolved pair is what matters.
        var split = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { ServerHeader = new ServerHeaderSettings { Value = "edge/1.0" } } },
            Hosts =
            [
                .. document.Hosts.Select(h => h with
                {
                    Settings = new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } },
                }),
            ],
        };
        Assert.Empty(ConfigValidator.Validate(split));

        // The global defaults are compiled on their own too, so a custom mode they leave without a value is refused there, even when every host supplies one.
        var defaultsWithoutValue = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } } },
            Hosts = [.. document.Hosts.Select(h => h with { Settings = new HostSettings { ServerHeader = new ServerHeaderSettings { Value = "edge/1.0" } } })],
        };
        AssertErrors(defaultsWithoutValue, ("/settings/defaults/serverHeader/value", ServerHeaderValueRequired));
        AssertErrors(
            new ConfigDocument(Settings: new GlobalSettings(Defaults: new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = string.Empty } })),
            ("/settings/defaults/serverHeader/value", ServerHeaderValueRequired));

        // A value is ignored while the resolved mode isn't custom, so an unusual one isn't refused then.
        HostSettings ignored = new() { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Remove, Value = "bad\tvalue" } };
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = ignored }) }));

        // But when a higher layer makes it custom, the value it inherits has to be usable.
        var used = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { ServerHeader = new ServerHeaderSettings { Value = "bad\tvalue" } } },
            Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } } }),
        };
        var inherited = Assert.Single(ConfigValidator.Validate(used));
        Assert.Equal(($"{Proxy}/settings/serverHeader/value", ServerHeaderValueInvalid), (inherited.Path, inherited.Code));
        Assert.Contains("inherited from the global defaults", inherited.Message, StringComparison.Ordinal);

        var fromProfile = document with
        {
            Profiles = [document.Profiles[0] with { Settings = new HostSettings { ServerHeader = new ServerHeaderSettings { Value = "bad\tvalue" } } }],
            Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom } } }),
        };
        var profileError = Assert.Single(ConfigValidator.Validate(fromProfile));
        Assert.Equal(($"{Proxy}/settings/serverHeader/value", ServerHeaderValueInvalid), (profileError.Path, profileError.Code));
        Assert.Contains("inherited from its profile", profileError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksHstsMaxAgeInEveryLayer()
    {
        var document = Valid();
        HostSettings tooLong = new() { Hsts = new HstsSettings { MaxAge = HstsHeader.MaxMaxAgeSeconds + 1 } };
        HostSettings negative = new() { Hsts = new HstsSettings { MaxAge = -1 } };
        Assert.True(HstsHeader.MaxMaxAgeSeconds == 63_072_000, "two years");

        AssertErrors(
            document with { Settings = document.Settings with { Defaults = tooLong } },
            ("/settings/defaults/hsts/maxAge", HstsMaxAgeInvalid));
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = negative }] },
            ("/profiles/0/settings/hsts/maxAge", HstsMaxAgeInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = tooLong }) },
            ($"{Proxy}/settings/hsts/maxAge", HstsMaxAgeInvalid));

        // 0 is valid, and means "tell browsers to forget the host"; the cap itself is valid too.
        foreach (var maxAge in new[] { 0, HstsHeader.DefaultMaxAgeSeconds, HstsHeader.MaxMaxAgeSeconds })
        {
            Assert.Empty(ConfigValidator.Validate(document with
            {
                Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = maxAge } } }),
            }));
        }
    }

    [Fact]
    public void PreloadNeedsIncludeSubDomainsAndAYear()
    {
        var document = Valid();
        static HostConfig WithHsts(HostConfig host, bool enabled, int? maxAge, bool? subdomains, bool? preload) =>
            host with { Settings = new HostSettings { Hsts = new HstsSettings { Enabled = enabled, MaxAge = maxAge, IncludeSubDomains = subdomains, Preload = preload } } };

        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => WithHsts(h, true, null, null, true)) },
            ($"{Proxy}/settings/hsts/preload", HstsPreloadNeedsIncludeSubDomains));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => WithHsts(h, true, 300, true, true)) },
            ($"{Proxy}/settings/hsts/preload", HstsPreloadMaxAgeTooShort));

        // Off, preload is inert: nothing is sent, so the list's rules refuse nothing.
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Hosts = MapHost(document, AppHost, h => WithHsts(h, false, 300, false, true)),
        }));

        // The pair may come from any layer: the host's preload with the profile's subdomains and the defaults' max-age.
        var layered = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = HstsHeader.DefaultMaxAgeSeconds } } },
            Profiles = [document.Profiles[0] with { Settings = new HostSettings { Hsts = new HstsSettings { IncludeSubDomains = true } } }],
            Hosts = MapHost(document, AppHost, h => WithHsts(h, true, null, null, true)),
        };
        Assert.Empty(ConfigValidator.Validate(layered));

        // A host that weakens what it inherits is where the error belongs: the subdomain and the max-age both.
        AssertErrors(
            layered with { Hosts = MapHost(layered, AppHost, h => h with { Settings = new HostSettings { Hsts = new HstsSettings { Preload = true, IncludeSubDomains = false } } }) },
            ($"{Proxy}/settings/hsts/preload", HstsPreloadNeedsIncludeSubDomains));
        AssertErrors(
            layered with { Hosts = MapHost(layered, AppHost, h => h with { Settings = new HostSettings { Hsts = new HstsSettings { Preload = true, MaxAge = 60 } } }) },
            ($"{Proxy}/settings/hsts/preload", HstsPreloadMaxAgeTooShort));
    }

    [Fact]
    public void PreloadIsJudgedOnEachHostsResolvedSettings()
    {
        // The profile asks for preload and the host supplies the subdomains: together that is valid, so neither is refused.
        var document = Valid();
        var split = document with
        {
            Profiles = [document.Profiles[0] with { Settings = new HostSettings { Hsts = new HstsSettings { Enabled = true, Preload = true } } }],
            Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { Hsts = new HstsSettings { IncludeSubDomains = true } } }),
        };
        Assert.Empty(ConfigValidator.Validate(split));

        // The same profile on a host that leaves includeSubDomains off is refused at that host, naming where preload came from.
        var broken = split with { Hosts = MapHost(split, AppHost, h => h with { Settings = new HostSettings() }) };
        var error = Assert.Single(ConfigValidator.Validate(broken));
        Assert.Equal(($"{Proxy}/settings/hsts/preload", HstsPreloadNeedsIncludeSubDomains), (error.Path, error.Code));
        Assert.Contains("inherited from its profile", error.Message, StringComparison.Ordinal);

        // A month is not a year, wherever the number came from.
        var tooShort = split with { Settings = split.Settings with { Defaults = new HostSettings { Hsts = new HstsSettings { MaxAge = 86_400 } } } };
        var shortError = Assert.Single(ConfigValidator.Validate(tooShort));
        Assert.Equal(HstsPreloadMaxAgeTooShort, shortError.Code);
        Assert.Contains("86400", shortError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALayerNoHostUsesIsNotRefusedForPreload()
    {
        // Nothing would send the header, so there is nothing to refuse: neither a profile no host picks...
        var document = Valid();
        var unusedProfile = document with
        {
            Profiles = [.. document.Profiles, new ProfileConfig(Id<ProfileId>(9), "preload", Settings: new HostSettings { Hsts = new HstsSettings { Enabled = true, Preload = true } })],
        };
        Assert.Empty(ConfigValidator.Validate(unusedProfile));

        // ... nor global defaults with no hosts under them.
        Assert.Empty(ConfigValidator.Validate(new ConfigDocument(
            Settings: new GlobalSettings(Defaults: new HostSettings { Hsts = new HstsSettings { Enabled = true, Preload = true } }))));

        // With a host under it, the host is the one refused (and points at the defaults it inherited from).
        ConfigDocument withHost = new(
            Settings: new GlobalSettings(Defaults: new HostSettings { Hsts = new HstsSettings { Enabled = true, Preload = true } }),
            Hosts: [new HostConfig(Id<HostId>(1), ["a.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200))]);
        var error = Assert.Single(ConfigValidator.Validate(withHost));
        Assert.Equal(("/hosts/0/settings/hsts/preload", HstsPreloadNeedsIncludeSubDomains), (error.Path, error.Code));
        Assert.Contains("inherited from the global defaults", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksProofOfWorkInEveryLayerAndRefusesRouteParameters()
    {
        var document = Valid();
        HostSettings valid = new() { ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = ProofOfWorkLimits.MinDifficulty, TokenLifetimeHours = 24 } };
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = valid } }));
        Assert.Empty(ConfigValidator.Validate(document with { Profiles = [document.Profiles[0] with { Settings = valid }] }));
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = valid }) }));

        HostSettings lowDifficulty = new() { ProofOfWork = new ProofOfWorkSettings { Difficulty = ProofOfWorkLimits.MinDifficulty - 1 } };
        HostSettings highDifficulty = new() { ProofOfWork = new ProofOfWorkSettings { Difficulty = ProofOfWorkLimits.MaxDifficulty + 1 } };
        HostSettings shortLifetime = new() { ProofOfWork = new ProofOfWorkSettings { TokenLifetimeHours = ProofOfWorkLimits.MinTokenLifetimeHours - 1 } };
        HostSettings longLifetime = new() { ProofOfWork = new ProofOfWorkSettings { TokenLifetimeHours = ProofOfWorkLimits.MaxTokenLifetimeHours + 1 } };
        AssertErrors(
            document with { Settings = document.Settings with { Defaults = lowDifficulty } },
            ("/settings/defaults/proofOfWork/difficulty", ProofOfWorkDifficultyInvalid));
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = highDifficulty }] },
            ("/profiles/0/settings/proofOfWork/difficulty", ProofOfWorkDifficultyInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = shortLifetime }) },
            ($"{Proxy}/settings/proofOfWork/tokenLifetimeHours", ProofOfWorkTokenLifetimeInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = longLifetime }) },
            ($"{Proxy}/settings/proofOfWork/tokenLifetimeHours", ProofOfWorkTokenLifetimeInvalid));

        // A route overrides only the switch: the difficulty and lifetime are the host's.
        var route = new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = true } });
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route] }) }));
        AssertErrors(
            document with
            {
                Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Difficulty = 16 } } }] }),
            },
            ($"{Proxy}/routes/0/settings/proofOfWork/difficulty", ProofOfWorkRouteDifficultyUnexpected));
        AssertErrors(
            document with
            {
                Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { ProofOfWork = new ProofOfWorkSettings { TokenLifetimeHours = 24 } } }] }),
            },
            ($"{Proxy}/routes/0/settings/proofOfWork/tokenLifetimeHours", ProofOfWorkRouteTokenLifetimeUnexpected));
    }

    [Fact]
    public void ChecksRequestLimitsInEveryLayerAndRefusesRouteHeaderLimits()
    {
        var document = Valid();
        HostSettings edges = new()
        {
            RequestLimits = new RequestLimitsSettings
            {
                MaxBodySize = RequestLimitBounds.MaxBodySize,
                MaxHeaderCount = RequestLimitBounds.MinHeaderCount,
                MaxHeaderBytes = RequestLimitBounds.MaxHeaderBytes,
                MaxUrlLength = RequestLimitBounds.MinUrlLength,
            },
        };
        HostSettings unlimited = new() { RequestLimits = new RequestLimitsSettings { MaxBodySize = RequestLimitBounds.UnlimitedBodySize } };
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = edges } }));
        Assert.Empty(ConfigValidator.Validate(document with { Profiles = [document.Profiles[0] with { Settings = unlimited }] }));
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = edges }) }));

        HostSettings negativeBody = new() { RequestLimits = new RequestLimitsSettings { MaxBodySize = -1 } };
        HostSettings hugeBody = new() { RequestLimits = new RequestLimitsSettings { MaxBodySize = RequestLimitBounds.MaxBodySize + 1 } };
        HostSettings fewHeaders = new() { RequestLimits = new RequestLimitsSettings { MaxHeaderCount = RequestLimitBounds.MinHeaderCount - 1 } };
        HostSettings manyHeaders = new() { RequestLimits = new RequestLimitsSettings { MaxHeaderCount = RequestLimitBounds.MaxHeaderCount + 1 } };
        HostSettings smallHeaders = new() { RequestLimits = new RequestLimitsSettings { MaxHeaderBytes = RequestLimitBounds.MinHeaderBytes - 1 } };
        HostSettings bigHeaders = new() { RequestLimits = new RequestLimitsSettings { MaxHeaderBytes = RequestLimitBounds.MaxHeaderBytes + 1 } };
        HostSettings shortUrl = new() { RequestLimits = new RequestLimitsSettings { MaxUrlLength = RequestLimitBounds.MinUrlLength - 1 } };
        HostSettings longUrl = new() { RequestLimits = new RequestLimitsSettings { MaxUrlLength = RequestLimitBounds.MaxUrlLength + 1 } };
        AssertErrors(
            document with { Settings = document.Settings with { Defaults = negativeBody } },
            ("/settings/defaults/requestLimits/maxBodySize", RequestLimitsMaxBodySizeInvalid));
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = hugeBody }] },
            ("/profiles/0/settings/requestLimits/maxBodySize", RequestLimitsMaxBodySizeInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = fewHeaders }) },
            ($"{Proxy}/settings/requestLimits/maxHeaderCount", RequestLimitsMaxHeaderCountInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = manyHeaders }) },
            ($"{Proxy}/settings/requestLimits/maxHeaderCount", RequestLimitsMaxHeaderCountInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = smallHeaders }) },
            ($"{Proxy}/settings/requestLimits/maxHeaderBytes", RequestLimitsMaxHeaderBytesInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = bigHeaders }) },
            ($"{Proxy}/settings/requestLimits/maxHeaderBytes", RequestLimitsMaxHeaderBytesInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = shortUrl }) },
            ($"{Proxy}/settings/requestLimits/maxUrlLength", RequestLimitsMaxUrlLengthInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = longUrl }) },
            ($"{Proxy}/settings/requestLimits/maxUrlLength", RequestLimitsMaxUrlLengthInvalid));

        // A route overrides only the body limit (even to none): the header and URL limits are judged before a route is known.
        var route = new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = RequestLimitBounds.UnlimitedBodySize } });
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route] }) }));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = -5 } } }] }) },
            ($"{Proxy}/routes/0/settings/requestLimits/maxBodySize", RequestLimitsMaxBodySizeInvalid));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxHeaderCount = 50 } } }] }) },
            ($"{Proxy}/routes/0/settings/requestLimits/maxHeaderCount", RequestLimitsRouteMaxHeaderCountUnexpected));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxHeaderBytes = 4096 } } }] }) },
            ($"{Proxy}/routes/0/settings/requestLimits/maxHeaderBytes", RequestLimitsRouteMaxHeaderBytesUnexpected));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [route with { Settings = new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxUrlLength = 1024 } } }] }) },
            ($"{Proxy}/routes/0/settings/requestLimits/maxUrlLength", RequestLimitsRouteMaxUrlLengthUnexpected));
    }

    [Fact]
    public void ChecksRobotsTextInEveryLayer()
    {
        var document = Valid();
        HostSettings valid = new()
        {
            Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "User-agent: *\nDisallow: /admin\r\n" },
        };
        HostSettings preset = new() { Robots = new RobotsSettings { Mode = RobotsMode.BlockAiCrawlers } };
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = valid } }));
        Assert.Empty(ConfigValidator.Validate(document with { Profiles = [document.Profiles[0] with { Settings = valid }] }));
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = valid }) }));
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = preset } }));

        // The resolved custom mode needs its text, wherever the mode is set; the error is at the host that would serve it.
        HostSettings missing = new() { Robots = new RobotsSettings { Mode = RobotsMode.Custom } };
        AssertErrors(
            document with { Settings = document.Settings with { Defaults = missing } },
            ($"{Proxy}/settings/robots/customText", RobotsCustomTextRequired),
            ($"{RedirectHostPath}/settings/robots/customText", RobotsCustomTextRequired),
            ($"{RespondHostPath}/settings/robots/customText", RobotsCustomTextRequired));
        Assert.Contains(
            "inherited from the global defaults",
            ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = missing } })[0].Message,
            StringComparison.Ordinal);
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = missing }] },
            ($"{Proxy}/settings/robots/customText", RobotsCustomTextRequired));
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = missing }) },
            ($"{Proxy}/settings/robots/customText", RobotsCustomTextRequired));

        // A blank text is no text.
        HostSettings blank = new() { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "  \r\n\t" } };
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = blank }) },
            ($"{Proxy}/settings/robots/customText", RobotsCustomTextRequired));

        // A layer may set the mode and let a lower layer supply the text.
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Settings = document.Settings with { Defaults = valid },
            Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { Robots = new RobotsSettings { Mode = RobotsMode.Custom } } }),
        }));

        // Text under any other mode is refused at the layer that sets it.
        HostSettings ignored = new() { Robots = new RobotsSettings { Mode = RobotsMode.DisallowAll, CustomText = "User-agent: *\nDisallow:\n" } };
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = ignored }] },
            ("/profiles/0/settings/robots/customText", RobotsCustomTextUnexpected));

        // A text may be set under a mode it inherits, so a layer that only sets the text is fine.
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { Robots = new RobotsSettings { CustomText = "User-agent: *\nDisallow:\n" } } }) }));

        HostSettings tooLong = new() { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = new string('a', RobotsText.MaxCustomBytes + 1) } };
        AssertErrors(
            document with { Settings = document.Settings with { Defaults = tooLong } },
            ("/settings/defaults/robots/customText", RobotsCustomTextTooLong));

        HostSettings control = new() { Robots = new RobotsSettings { Mode = RobotsMode.Custom, CustomText = "User-agent: *\nDis\u0000allow: /\n" } };
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = control }) },
            ($"{Proxy}/settings/robots/customText", RobotsCustomTextInvalid));

        // An undefined mode is the enum's error, not a text one.
        HostSettings undefined = new() { Robots = new RobotsSettings { Mode = (RobotsMode)9 } };
        AssertErrors(
            document with { Hosts = MapHost(document, AppHost, h => h with { Settings = undefined }) },
            ($"{Proxy}/settings/robots/mode", EnumUndefined));
    }

    /// <summary>Validates <paramref name="document"/> as written and expects exactly these errors, in order.</summary>
    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected)
    {
        var errors = ConfigValidator.Validate(document);
        Assert.Equal(expected, errors.Select(e => (e.Path, e.Code)));
    }
    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        MapHost(document.Hosts, id, change);

    private static HostConfig[] MapHost(IReadOnlyList<HostConfig> hosts, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. hosts.Select(h => h.Id == id ? change(h) : h)];
}
