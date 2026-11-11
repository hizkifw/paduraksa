using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.AccessLists;

/// <summary>The country is part of the address side of an access list: a denied country is refused, a non-empty allow list admits only
/// its countries, and both combine with the addresses and the passwords as the list's <c>satisfy</c> says.</summary>
public sealed class AccessListCountryTests
{
    private const string Inside = "10.1.1.1";
    private const string Outside = "192.0.2.1";
    private const string Vetoed = "10.9.1.1";

    private static readonly string[] Allow = ["10.0.0.0/8"];
    private static readonly string[] Deny = ["10.9.0.0/16"];
    private static readonly string[] AllowedCountries = ["DE", "NL"];
    private static readonly string[] DeniedCountries = ["RU"];

    private static readonly string[] Clients = [Inside, Outside, Vetoed];

    // DE is allowed, FR isn't listed, RU is denied; XX is how Cloudflare says "unknown", null is no header at all.
    private static readonly string?[] Countries = ["DE", "FR", "RU", "XX", "T1", null];

    public static TheoryData<AccessListSatisfy, string, string?, string> Verdicts
    {
        get
        {
            TheoryData<AccessListSatisfy, string, string?, string> rows = [];
            foreach (var satisfy in new[] { AccessListSatisfy.All, AccessListSatisfy.Any })
            {
                foreach (var client in Clients)
                {
                    foreach (var country in Countries)
                    {
                        foreach (var credentials in new[] { "none", "good" })
                        {
                            rows.Add(satisfy, client, country, credentials);
                        }
                    }
                }
            }

            return rows;
        }
    }

    /// <summary>The whole table in words. A denied address or country is refused whatever is sent. Otherwise the visitor is allowed when
    /// the address is in the allow list and the country is in the allowed countries; <c>all</c> then asks for the password and
    /// refuses the rest with 403, <c>any</c> lets the allowed through and asks the rest for it.</summary>
    private static string Expected(AccessListSatisfy satisfy, string client, string? country, string credentials)
    {
        if (client == Vetoed || country == "RU")
        {
            return "403";
        }

        var allowed = client == Inside && country is "DE" or "NL";
        if (satisfy == AccessListSatisfy.Any)
        {
            return allowed || credentials == "good" ? "pass" : "401";
        }

        return !allowed ? "403" : credentials == "good" ? "pass" : "401";
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public async Task CountriesAddressesPasswordsAndSatisfyCombine(AccessListSatisfy satisfy, string client, string? country, string credentials)
    {
        var list = List("office", Allow, Deny, satisfy: satisfy, allowCountries: AllowedCountries, denyCountries: DeniedCountries);
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(list, cloudflare: true), out _);

        var result = await RunAsync(gate, ViaCloudflare(client, country, credentials == "good" ? Basic("alice", Password) : null));

        Assert.Equal(Expected(satisfy, client, country, credentials), result);
    }

    [Theory]
    [InlineData("DE", "pass")]
    [InlineData("nl", "pass")]
    [InlineData("FR", "403")]
    [InlineData("T1", "403")]
    [InlineData("XX", "403")]
    [InlineData("garbage", "403")]
    [InlineData(null, "403")]
    public async Task AnAllowListOfCountriesAdmitsOnlyThemAndNeverAnUnknownOne(string? country, string expected)
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("eu", users: false, allowCountries: AllowedCountries), cloudflare: true), out _);

        Assert.Equal(expected, await RunAsync(gate, ViaCloudflare(Outside, country)));
    }

    [Theory]
    [InlineData("XX", "pass")]
    [InlineData(null, "pass")]
    [InlineData("", "pass")]
    [InlineData("DE", "pass")]
    [InlineData("FR", "403")]
    [InlineData("T1", "403")]
    public async Task AnUnknownCountryPassesAnAllowListOnlyWhenXxIsAllowed(string? country, string expected)
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("eu", users: false, allowCountries: ["DE", "XX"]), cloudflare: true), out _);

        Assert.Equal(expected, await RunAsync(gate, ViaCloudflare(Outside, country)));
    }

    [Theory]
    [InlineData("T1", "403")]
    [InlineData("t1", "403")]
    [InlineData("XX", "pass")]
    [InlineData(null, "pass")]
    [InlineData("DE", "pass")]
    public async Task TorCanBeBlockedOnItsOwnWithoutBlockingUnknown(string? country, string expected)
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("no-tor", users: false, denyCountries: ["T1"]), cloudflare: true), out _);

        Assert.Equal(expected, await RunAsync(gate, ViaCloudflare(Outside, country)));
    }

    [Theory]
    [InlineData("T1", "pass")]
    [InlineData("DE", "403")]
    [InlineData("XX", "403")]
    public async Task TorCanBeTheOnlyAllowedCountry(string country, string expected)
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("tor-only", users: false, allowCountries: ["T1"]), cloudflare: true), out _);

        Assert.Equal(expected, await RunAsync(gate, ViaCloudflare(Outside, country)));
    }

    [Fact]
    public async Task AnUnknownCountryCanBeDenied()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("known", users: false, denyCountries: ["XX"]), cloudflare: true), out _);

        Assert.Equal("403", await RunAsync(gate, ViaCloudflare(Outside, "XX")));
        Assert.Equal("403", await RunAsync(gate, ViaCloudflare(Outside, null)));
        Assert.Equal("pass", await RunAsync(gate, ViaCloudflare(Outside, "DE")));
    }

    [Fact]
    public async Task AForgedHeaderFromAnUntrustedPeerIsIgnored()
    {
        // Allow DE: a stranger claiming DE is still of unknown country and refused. Deny RU: a stranger claiming RU is not refused for it.
        var allow = GateFor(AccessListFixtures.NewRuntime(), Model(List("de", users: false, allowCountries: ["DE"]), cloudflare: true), out _);
        var deny = GateFor(AccessListFixtures.NewRuntime(), Model(List("no-ru", users: false, denyCountries: ["RU"]), cloudflare: true), out _);

        Assert.Equal("pass", await RunAsync(allow, ViaCloudflare(Outside, "DE")));
        Assert.Equal("403", await RunAsync(allow, ViaCloudflare(Outside, "DE", peer: "203.0.113.50")));
        Assert.Equal("403", await RunAsync(deny, ViaCloudflare(Outside, "RU")));
        Assert.Equal("pass", await RunAsync(deny, ViaCloudflare(Outside, "RU", peer: "203.0.113.50")));
    }

    [Fact]
    public async Task WithoutACloudflareModeEveryCountryIsUnknownEvenFromCloudflareAddresses()
    {
        var allow = GateFor(AccessListFixtures.NewRuntime(), Model(List("de", users: false, allowCountries: ["DE"])), out _);
        var deny = GateFor(AccessListFixtures.NewRuntime(), Model(List("no-ru", users: false, denyCountries: ["RU"])), out _);

        Assert.Equal("403", await RunAsync(allow, ViaCloudflare(Outside, "DE")));
        Assert.Equal("pass", await RunAsync(deny, ViaCloudflare(Outside, "RU")));
    }

    [Fact]
    public async Task ADeniedCountryIsDroppedWhenTheListDrops()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("quiet", users: false, drop: true, denyCountries: DeniedCountries), cloudflare: true), out _);

        Assert.Equal("drop", await RunAsync(gate, ViaCloudflare(Outside, "RU")));
        Assert.Equal("pass", await RunAsync(gate, ViaCloudflare(Outside, "DE")));
    }

    [Fact]
    public async Task APasswordLetsAVisitorFromACountryOutsideTheAllowListInButNotFromADeniedOne()
    {
        var list = List("office", satisfy: AccessListSatisfy.Any, allowCountries: AllowedCountries, denyCountries: DeniedCountries);
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(list, cloudflare: true), out _);
        var good = Basic("alice", Password);

        Assert.Equal("pass", await RunAsync(gate, ViaCloudflare(Outside, "DE")));
        Assert.Equal("401", await RunAsync(gate, ViaCloudflare(Outside, "FR")));
        Assert.Equal("pass", await RunAsync(gate, ViaCloudflare(Outside, "FR", good)));
        Assert.Equal("403", await RunAsync(gate, ViaCloudflare(Outside, "RU", good)));
    }

    [Fact]
    public void TheHostChainStageRefusesByCountryAndLeavesPasswordsToTheRoute()
    {
        var list = List("office", satisfy: AccessListSatisfy.All, allowCountries: AllowedCountries);
        ProxyConfigModel model = new()
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts = [new HostSpec
            {
                Id = TestIds.Host("acl.test"),
                Names = ["acl.test"],
                Policies = [new AccessListPolicySpec(list.Id, AccessListStage.AddressOnly)],
                Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
            }],
        };
        var gate = GateFor(AccessListFixtures.NewRuntime(), model, out _);

        Assert.Equal(GateAction.Respond, gate.Decide(ViaCloudflare(Outside, "FR")).Action);
        Assert.Equal(403, gate.Decide(ViaCloudflare(Outside, "FR")).Response!.StatusCode);
        Assert.Equal(401, gate.Decide(ViaCloudflare(Outside, "DE")).Response!.StatusCode);
    }

    [Fact]
    public void TheUnionOfListsAdmitsACountryAnyOfThemAdmits()
    {
        var germany = List("de", users: false, allowCountries: ["DE"]);
        var france = List("fr", users: false, allowCountries: ["FR"]);
        ProxyConfigModel model = new()
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [germany, france],
            Hosts = [new HostSpec
            {
                Id = TestIds.Host("acl.test"),
                Names = ["acl.test"],
                Policies = [new AccessListUnionPolicySpec([germany.Id, france.Id])],
                Routes = [TestSnapshots.Route("root", "web")],
            }],
        };
        var gate = GateFor(AccessListFixtures.NewRuntime(), model, out _);

        Assert.Equal(GateAction.Forward, gate.Decide(ViaCloudflare(Outside, "DE")).Action);
        Assert.Equal(GateAction.Forward, gate.Decide(ViaCloudflare(Outside, "FR")).Action);
        Assert.Equal(GateAction.Respond, gate.Decide(ViaCloudflare(Outside, "NL")).Action);
        Assert.Equal(GateAction.Respond, gate.Decide(ViaCloudflare(Outside, null)).Action);
    }

    [Fact]
    public void TheCompiledListHoldsOneBitPerCountryAndNamesPseudoCodesToo()
    {
        var list = List("mixed", allowCountries: ["de", "T1"], denyCountries: ["XX", "RU"]);
        GateFor(AccessListFixtures.NewRuntime(), Model(list), out var snapshot);

        var compiled = ((AccessListPolicy)snapshot.HostsByIndex[0].Routes[0].Policies[0]).List;

        Assert.Equal(2, compiled.AllowCountries.Count);
        Assert.True(compiled.AllowCountries.Contains(CountryCode.Parse("DE")));
        Assert.True(compiled.AllowCountries.Contains(CountryCode.Tor));
        Assert.False(compiled.AllowCountries.Contains(CountryCode.Unknown));
        Assert.True(compiled.DenyCountries.Contains(CountryCode.Unknown));
        Assert.True(compiled.DenyCountries.Contains(CountryCode.Parse("RU")));
        Assert.False(compiled.DenyCountries.Contains(CountryCode.Tor));
    }

    [Fact]
    public void ACodeTheCompilerDoesNotKnowIsACompileError()
    {
        var model = Model(List("bad", allowCountries: ["DE", "ZZ"], denyCountries: ["usa"]));

        var errors = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions { AccessLists = AccessListFixtures.NewRuntime() }).Errors;

        Assert.Contains(errors, e => e.Message.Contains("'ZZ' isn't a country code", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("'usa' isn't a country code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryVerdictByCountryAllocatesNothing()
    {
        var runtime = AccessListFixtures.NewRuntime();
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var europe = List("europe", users: false, allowCountries: AllowedCountries);
        var quiet = List("quiet", users: false, drop: true, denyCountries: ["T1"]);
        var passwords = List("passwords", satisfy: AccessListSatisfy.Any, allowCountries: AllowedCountries);
        ProxyConfigModel model = new()
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [europe, quiet, passwords],
            Hosts = [Host("europe.test", europe), Host("quiet.test", quiet), Host("passwords.test", passwords)],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);

        // The one PBKDF2 a password costs, through the whole gate, so the measured requests find it remembered.
        Assert.Equal("pass", await RunAsync(new GateMiddleware(store, addresses), ViaCloudflare(Outside, "FR", Basic("alice", Password), "passwords.test")));

        var cases = new (string Name, DefaultHttpContext Context, GateAction Expected)[]
        {
            ("an allowed country", ViaCloudflare(Outside, "DE", host: "europe.test"), GateAction.Forward),
            ("a country outside the allow list (403)", ViaCloudflare(Outside, "FR", host: "europe.test"), GateAction.Respond),
            ("an unknown country (403)", ViaCloudflare(Outside, "XX", host: "europe.test"), GateAction.Respond),
            ("a missing header (403)", ViaCloudflare(Outside, null, host: "europe.test"), GateAction.Respond),
            ("a forged header from an untrusted peer (403)", ViaCloudflare(Outside, "DE", host: "europe.test", peer: "203.0.113.50"), GateAction.Respond),
            ("Tor, dropped", ViaCloudflare(Outside, "T1", host: "quiet.test"), GateAction.Abort),
            ("a country the password lets in", ViaCloudflare(Outside, "FR", Basic("alice", Password), "passwords.test"), GateAction.Forward),
            ("a country asked for a password (401)", ViaCloudflare(Outside, "FR", host: "passwords.test"), GateAction.Respond),
        };
        foreach (var (name, context, expected) in cases)
        {
            Assert.Equal(expected, GateMiddleware.Decide(context, snapshot, addresses, null, default(NoGateTracer)).Action);
            AllocAssert.Zero((snapshot, addresses, context), static s => _ = GateMiddleware.Decide(s.context, s.snapshot, s.addresses, null, default(NoGateTracer)), name);
        }
    }

    private static HostSpec Host(string name, AccessListSpec list) => new()
    {
        Id = TestIds.Host(name),
        Names = [name],
        Routes = [TestSnapshots.Route(name, "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
    };
}
