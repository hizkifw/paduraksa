using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

public sealed class ConfigValidatorTests
{
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
        ConfigDocument document = Valid();
        ConfigDocument unsorted = document with
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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
        string text = AppUpstream.ToString();
        int letter = text.AsSpan(UpstreamId.Prefix.Length + 1).LastIndexOfAnyInRange('A', 'Z') + UpstreamId.Prefix.Length + 1;
        Assert.True(letter > UpstreamId.Prefix.Length, "the sample ID needs an upper-case letter");
        UpstreamId twin = UpstreamId.Parse(string.Concat(text.AsSpan(0, letter), char.ToLowerInvariant(text[letter]).ToString(), text.AsSpan(letter + 1)), null);
        Assert.NotEqual(AppUpstream, twin);

        ConfigDocument document = Valid();
        ConfigDocument changed = document with
        {
            Upstreams = [.. document.Upstreams, new UpstreamConfig(twin, "twin", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://b")])],
        };

        AssertErrors(changed, ("/upstreams/1/id", IdDuplicate));
    }

    [Fact]
    public void RouteIdsAreUniqueAcrossHosts()
    {
        ConfigDocument document = Valid();
        HostConfig other = new(
            Id<HostId>(9),
            ["other.example.com"],
            Routes: [new RouteConfig(RootRoute, AppUpstream)]);

        AssertErrors(document with { Hosts = [.. document.Hosts, other] }, ("/hosts/3/routes/0/id", IdDuplicate));
    }

    [Fact]
    public void RejectsNegativeVersions()
    {
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
        {
            Settings = document.Settings with { Version = -1 },
            Hosts = MapHost(document, RespondHost, h => h with { Version = -3 }),
        };

        AssertErrors(changed, ("/settings/version", VersionNegative), ($"{RespondHostPath}/version", VersionNegative));
    }

    [Fact]
    public void RejectsUndefinedEnums()
    {
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
        ConfigDocument kind = document with { Hosts = MapHost(document, RespondHost, h => h with { Kind = (HostKind)9 }) };

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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
        ConfigDocument document = Valid();
        UpstreamConfig upstream = document.Upstreams[0] with
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
    public void AnUpstreamAnEnabledHostRoutesToNeedsAnEnabledDestination()
    {
        ConfigDocument document = Valid();
        UpstreamConfig off = document.Upstreams[0] with
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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
        ConfigDocument document = Valid();
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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
    public void ChecksCertificateBindings()
    {
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
        {
            Settings = document.Settings with { DefaultCertificateId = default(CertificateId) },
            Hosts = MapHost(
                MapHost(document, AppHost, h => h with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific } }),
                RedirectHost,
                h => h with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None, CertificateId = Id<CertificateId>(1) } }),
        };

        AssertErrors(
            changed,
            ("/settings/defaultCertificateId", IdEmpty),
            ($"{Proxy}/certificate/certificateId", HostCertificateIdRequired),
            ($"{RedirectHostPath}/certificate/certificateId", HostCertificateIdUnexpected));
    }

    [Fact]
    public void ChecksKindSpecificParts()
    {
        ConfigDocument document = Valid();
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
        ConfigDocument document = Valid();

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
        ConfigDocument document = Valid();
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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with { Hosts = MapHost(document, AppHost, h => h with { Routes = [h.Routes[0] with { Path = path }] }) };

        AssertErrors(changed, ($"{Proxy}/routes/0/path", code));
    }

    [Fact]
    public void RejectsRoutesThatCanNeverMatch()
    {
        ConfigDocument document = Valid();
        static RouteConfig Route(int id, string path, RouteMatchKind match = RouteMatchKind.Prefix) => new(Id<RouteId>(id), AppUpstream, Path: path, Match: match);
        ConfigDocument changed = document with
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
        ConfigDocument document = Valid();
        foreach (string path in new[] { "/", "/api", "/api/", "/a/b.c/d", "/ÄPI" })
        {
            Assert.Empty(ConfigValidator.Validate(document with
            {
                Hosts = MapHost(document, AppHost, h => h with { Routes = [h.Routes[0] with { Path = path }] }),
            }));
        }
    }

    [Fact]
    public void ChecksAllowListsInEveryLayer()
    {
        ConfigDocument document = Valid();
        IpAllowListSettings bad = new(["10.0.0.0/8", "10.0.0.0/33", "nope"]);
        HostSettings settings = new() { IpAllowList = bad };
        ConfigDocument changed = document with
        {
            Settings = document.Settings with { Defaults = settings },
            Profiles = [document.Profiles[0] with { Settings = new HostSettings { IpAllowList = new IpAllowListSettings([], DropDenied: true) } }],
            Hosts = MapHost(document, AppHost, h => h with
            {
                Settings = settings,
                Routes = [h.Routes[0] with { Settings = new RouteSettings { IpAllowList = bad } }],
            }),
        };

        AssertErrors(
            changed,
            ("/settings/defaults/ipAllowList/addresses/1", AddressInvalid),
            ("/settings/defaults/ipAllowList/addresses/2", AddressInvalid),
            ("/profiles/0/settings/ipAllowList/dropDenied", IpAllowListDropDeniedWithoutAddresses),
            ($"{Proxy}/settings/ipAllowList/addresses/1", AddressInvalid),
            ($"{Proxy}/settings/ipAllowList/addresses/2", AddressInvalid),
            ($"{Proxy}/routes/0/settings/ipAllowList/addresses/1", AddressInvalid),
            ($"{Proxy}/routes/0/settings/ipAllowList/addresses/2", AddressInvalid));

        // An empty list lifts an inherited one.
        Assert.Empty(ConfigValidator.Validate(document with
        {
            Hosts = MapHost(document, AppHost, h => h with { Settings = new HostSettings { IpAllowList = new IpAllowListSettings([]) } }),
        }));
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
        ConfigDocument document = Valid();
        TrustedProxySettings trusted = new(mode, proxies, header, trueClientIp, forwardedForFallback, forwardedProto);

        AssertErrors(document with { Settings = document.Settings with { TrustedProxies = trusted } }, ($"/settings/trustedProxies{field}", code));
    }

    [Fact]
    public void ChecksTags()
    {
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
        {
            Hosts = MapHost(document, AppHost, h => h with { Tags = [" Media ", "k8s:prod/eu", "", "two words", new string('t', 65)] }),
        };

        AssertErrors(changed, ($"{Proxy}/tags/2", HostTagInvalid), ($"{Proxy}/tags/3", HostTagInvalid), ($"{Proxy}/tags/4", HostTagInvalid));
    }

    [Fact]
    public void ChecksLengthsAndControlCharacters()
    {
        ConfigDocument document = Valid();
        string notes = new('n', ConfigValidator.MaxNotesLength + 1);
        ConfigDocument changed = document with
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
        ConfigDocument document = Valid();
        ConfigDocument changed = document with
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
        ConfigDocument document = Valid();
        ConfigDocument broken = document with
        {
            ApiVersion = "v0",
            Upstreams = [],
            Hosts = MapHost(document, AppHost, h => h with { Names = ["-bad-"] }),
        };

        IReadOnlyList<ConfigError> errors = ConfigValidator.Validate(broken);

        Assert.Equal(4, errors.Count); // apiVersion, the name, two routes to a missing upstream
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        Assert.Contains(errors, e => e.Message.Contains("-bad-", StringComparison.Ordinal));
    }

    /// <summary>Validates <paramref name="document"/> as written and expects exactly these errors, in order.</summary>
    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected)
    {
        IReadOnlyList<ConfigError> errors = ConfigValidator.Validate(document);
        Assert.Equal(expected, errors.Select(e => (e.Path, e.Code)));
    }

    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        MapHost(document.Hosts, id, change);

    private static HostConfig[] MapHost(IReadOnlyList<HostConfig> hosts, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. hosts.Select(h => h.Id == id ? change(h) : h)];
}
