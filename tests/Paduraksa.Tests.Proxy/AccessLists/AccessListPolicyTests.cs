using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.AccessLists;

public sealed class AccessListPolicyTests
{
    private const string Inside = "10.1.1.1";
    private const string Outside = "192.0.2.1";
    private const string Vetoed = "10.9.1.1";

    private static readonly string[] Allow = ["10.0.0.0/8"];
    private static readonly string[] Deny = ["10.9.0.0/16"];

    public static TheoryData<AccessListSatisfy, string, string, string> Verdicts => new()
    {
        { AccessListSatisfy.All, Inside, "none", "401" },
        { AccessListSatisfy.All, Inside, "bad", "401" },
        { AccessListSatisfy.All, Inside, "good", "pass" },
        { AccessListSatisfy.All, Outside, "none", "403" },
        { AccessListSatisfy.All, Outside, "bad", "403" },
        { AccessListSatisfy.All, Outside, "good", "403" },
        { AccessListSatisfy.All, Vetoed, "good", "403" },
        { AccessListSatisfy.Any, Inside, "none", "pass" },
        { AccessListSatisfy.Any, Inside, "bad", "pass" },
        { AccessListSatisfy.Any, Inside, "good", "pass" },
        { AccessListSatisfy.Any, Outside, "none", "401" },
        { AccessListSatisfy.Any, Outside, "bad", "401" },
        { AccessListSatisfy.Any, Outside, "good", "pass" },
        { AccessListSatisfy.Any, Vetoed, "good", "403" },
        { AccessListSatisfy.Any, Vetoed, "none", "403" },
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public async Task SatisfyCombinesTheAddressAndThePassword(AccessListSatisfy satisfy, string client, string credentials, string expected)
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("office", Allow, Deny, satisfy: satisfy)), out _);
        var authorization = credentials switch
        {
            "good" => Basic("alice", Password),
            "bad" => Basic("alice", "wrong"),
            _ => null,
        };

        Assert.Equal(expected, await RunAsync(gate, client, authorization));
    }

    [Theory]
    [InlineData("192.0.2.1", true)]
    [InlineData("::ffff:192.0.2.1", true)] // the IPv4-mapped spelling (TCP on [::]) is the same client
    [InlineData("10.200.0.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("192.0.2.2", false)]
    [InlineData("2001:db9::1", false)]
    public void AnAddressListUsesTheResolvedClientAddress(string client, bool allowed)
    {
        var list = List("lan", ["192.0.2.1", "10.0.0.0/8", "2001:db8::/32"], users: false);
        var snapshot = TestSnapshots.Bound(Model(list), options: new SnapshotCompilerOptions { AccessLists = AccessListFixtures.NewRuntime() });
        var policy = snapshot.HostsByIndex[0].Routes[0].Policies[0];
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse(client), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        var request = new RequestView(Context("192.0.2.1"), state);

        Assert.Equal(allowed ? PolicyAction.Continue : PolicyAction.Respond, policy.Evaluate(in request).Action);
    }

    [Fact]
    public async Task WithoutUsersOnlyTheAddressApplies()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("lan", Allow, Deny, users: false)), out _);

        Assert.Equal("pass", await RunAsync(gate, Inside));
        Assert.Equal("403", await RunAsync(gate, Outside));
        Assert.Equal("403", await RunAsync(gate, Vetoed));
    }

    [Fact]
    public async Task WithoutAddressRulesOnlyThePasswordApplies()
    {
        foreach (var satisfy in new[] { AccessListSatisfy.All, AccessListSatisfy.Any })
        {
            var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("open", satisfy: satisfy)), out _);

            Assert.Equal("401", await RunAsync(gate, Outside));
            Assert.Equal("pass", await RunAsync(gate, Outside, Basic("bob", "battery staple")));
        }
    }

    [Fact]
    public async Task ADenyEntryAloneRefusesThatAddressAndAsksEveryoneElseForAPassword()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("veto", deny: Deny, satisfy: AccessListSatisfy.Any)), out _);

        Assert.Equal("403", await RunAsync(gate, Vetoed, Basic("alice", Password)));
        Assert.Equal("401", await RunAsync(gate, Outside));
        Assert.Equal("pass", await RunAsync(gate, Outside, Basic("alice", Password)));
    }

    [Fact]
    public async Task DropDeniedAbortsAnAddressTheListRefusesButStillAsksForAPassword()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("quiet", Allow, Deny, drop: true)), out _);

        Assert.Equal("drop", await RunAsync(gate, Outside));
        Assert.Equal("drop", await RunAsync(gate, Vetoed, Basic("alice", Password)));
        Assert.Equal("401", await RunAsync(gate, Inside));
        Assert.Equal("pass", await RunAsync(gate, Inside, Basic("alice", Password)));
    }

    [Fact]
    public void TheChallengeIsPrecomputedWithTheRealmAndNoStore()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("Staff \"area\"", realm: "Staff \"area\"")), out _);

        var response = Decide(gate, Outside).Response!;

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Basic realm=\"Staff \\\"area\\\"\", charset=\"UTF-8\"", Assert.Single(response.Headers, h => h.Name == "WWW-Authenticate").Value.ToString());
        Assert.Equal("no-store", response.CacheControl.ToString());
        Assert.NotNull(response.HtmlPage);
    }

    [Fact]
    public void ABackslashInTheRealmIsEscapedInTheChallenge()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("slashes", realm: "a\\b")), out _);

        var response = Decide(gate, Outside).Response!;

        Assert.Equal("Basic realm=\"a\\\\b\", charset=\"UTF-8\"", Assert.Single(response.Headers, h => h.Name == "WWW-Authenticate").Value.ToString());
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Basic")]
    [InlineData("Basic !!!not-base64")]
    [InlineData("Basic YWxpY2U=")]
    [InlineData("Basic Om5vLW5hbWU=")]
    public async Task CredentialsThatCannotBeReadAreNotAPassword(string header)
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        Assert.Equal("401", await RunAsync(gate, Outside, header));
        Assert.Equal(0, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AnotherHeaderLineIsNotAPasswordEither()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("office")), out _);
        var context = Context(Outside);
        context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues([Basic("alice", Password), Basic("alice", Password)]);

        Assert.Equal("401", await RunAsync(gate, context));
    }

    [Fact]
    public async Task ThePasswordMayContainColonsAndNonAsciiCharacters()
    {
        AccessListSpec list = List("office") with { Users = [new AccessListUserSpec("carol", TestPasswords.Credential("pa:ss:wörd"))] };
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(list), out _);

        Assert.Equal("pass", await RunAsync(gate, Outside, Basic("carol", "pa:ss:wörd")));
        Assert.Equal("401", await RunAsync(gate, Outside, Basic("carol", "pa")));
    }

    [Fact]
    public async Task NamesAreCaseSensitive()
    {
        var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("office")), out _);

        Assert.Equal("401", await RunAsync(gate, Outside, Basic("Alice", Password)));
    }

    [Fact]
    public async Task AVerifiedPasswordIsRememberedAndNeedsNoSecondPbkdf2()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        Assert.Equal("pass", await RunAsync(gate, Outside, credentials));
        Assert.Equal("pass", await RunAsync(gate, Outside, credentials));
        Assert.Equal("pass", await RunAsync(gate, Outside, credentials));

        Assert.Equal(1, runtime.Stats.Verifications);
        Assert.Equal(2, runtime.Stats.CacheHits);
    }

    [Fact]
    public async Task ARememberedPasswordExpiresFiveMinutesAfterItsLastUse()
    {
        StepClock clock = new();
        var runtime = AccessListFixtures.NewRuntime(clock);
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        await RunAsync(gate, Outside, credentials);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await RunAsync(gate, Outside, credentials);
        Assert.Equal(1, runtime.Stats.Verifications);

        // The hit pushed the expiry out: five minutes from the hit, not from the first check.
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await RunAsync(gate, Outside, credentials);
        Assert.Equal(1, runtime.Stats.Verifications);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal("pass", await RunAsync(gate, Outside, credentials));
        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AWrongPasswordIsNeverRemembered()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("401", await RunAsync(gate, Outside, Basic("alice", "wrong")));
        }

        Assert.Equal(3, runtime.Stats.Verifications);
        Assert.Equal(0, runtime.Stats.CacheHits);
        Assert.Equal(0, AccessListFixtures.Cached(runtime));
    }

    [Fact]
    public async Task AnUnknownNameCostsAVerificationToo()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        Assert.Equal("401", await RunAsync(gate, Outside, Basic("mallory", Password)));

        Assert.Equal(1, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AnotherAddressDoesNotShareARememberedPassword()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        await RunAsync(gate, "192.0.2.1", credentials);
        await RunAsync(gate, "192.0.2.2", credentials);

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AnIpv6ClientIsRememberedByItsSlash64()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        await RunAsync(gate, "2001:db8:1:2::1", credentials);
        await RunAsync(gate, "2001:db8:1:2::ffff", credentials);
        await RunAsync(gate, "2001:db8:1:3::1", credentials);

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AChangedPasswordDoesNotReuseAnEntryOfTheOldOneAndNeedsNoRecompile()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var credential = TestPasswords.Credential(Password);
        AccessListSpec list = List("office") with { Users = [new AccessListUserSpec("alice", credential)] };
        var gate = GateFor(runtime, Model(list), out _);
        var credentials = Basic("alice", Password);
        Assert.Equal("pass", await RunAsync(gate, Outside, credentials));

        // The vault replaces the password under the handle the snapshot already holds: a new hash and salt, so a new fingerprint.
        TestPasswords.Change(credential, "a new password");

        Assert.Equal("401", await RunAsync(gate, Outside, credentials));
        Assert.Equal("pass", await RunAsync(gate, Outside, Basic("alice", "a new password")));
    }

    [Fact]
    public async Task AUserWhoseCredentialHasNoPasswordCanNotSignIn()
    {
        var runtime = AccessListFixtures.NewRuntime();
        AccessListSpec list = List("office") with { Users = [new AccessListUserSpec("alice", TestPasswords.Credential(null))] };
        var gate = GateFor(runtime, Model(list), out _);

        Assert.Equal("401", await RunAsync(gate, Outside, Basic("alice", Password)));
        Assert.Equal(0, AccessListFixtures.Cached(runtime));
    }

    [Fact]
    public async Task TwoListsWithTheirOwnCredentialsForTheSameNameAndPasswordDoNotShareAnEntry()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var one = List("one");
        var two = List("two") with { Users = [new AccessListUserSpec("alice", TestPasswords.Credential(Password))] };
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [one, two],
            Hosts =
            [
                new HostSpec { Id = TestIds.Host("a"), Names = ["a.test"], Routes = [TestSnapshots.Route("a", "web") with { Policies = [new AccessListPolicySpec(one.Id)] }] },
                new HostSpec { Id = TestIds.Host("b"), Names = ["b.test"], Routes = [TestSnapshots.Route("b", "web") with { Policies = [new AccessListPolicySpec(two.Id)] }] },
            ],
        };
        var gate = GateFor(runtime, model, out _);
        var credentials = Basic("alice", Password);

        await RunAsync(gate, Outside, credentials, "a.test");
        await RunAsync(gate, Outside, credentials, "b.test");

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task TheCredentialsAreStrippedFromWhatTheUpstreamGetsOnlyWhenTheListConsumedThem()
    {
        foreach (var (pass, expected) in new[] { (false, true), (true, false) })
        {
            var gate = GateFor(AccessListFixtures.NewRuntime(), Model(List("office", passUpstream: pass)), out _);
            var context = Context(Outside, Basic("alice", Password));

            Assert.Equal("pass", await RunAsync(gate, context));
            Assert.Equal(expected, context.Features.Get<IGateFeature>()!.State.StripAuthorization);
        }

        // A list that let the address in without asking never consumed a header the upstream may need.
        var lan = GateFor(AccessListFixtures.NewRuntime(), Model(List("lan", Allow, satisfy: AccessListSatisfy.Any)), out _);
        var ignored = Context(Inside, "Bearer for-the-upstream");
        Assert.Equal(GateAction.Forward, lan.Decide(ignored).Action);
        Assert.False(ignored.Features.Get<IGateFeature>()!.State.StripAuthorization);
    }

    [Fact]
    public async Task AHostChainListOnlyRefusesByAddressAndLeavesPasswordsToTheRoute()
    {
        var list = List("office", Allow, satisfy: AccessListSatisfy.All);
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts = [new HostSpec
            {
                Id = TestIds.Host("h"),
                Names = ["acl.test"],
                Policies = [new AccessListPolicySpec(list.Id, AccessListStage.AddressOnly)],
                Routes = [TestSnapshots.Route("root", "web")],
            }],
        };
        var gate = GateFor(AccessListFixtures.NewRuntime(), model, out _);

        Assert.Equal("403", await RunAsync(gate, Outside));
        Assert.Equal("pass", await RunAsync(gate, Inside));
    }

    [Fact]
    public async Task TheHostChainUnionRefusesOnlyWhatEveryRoutesListRefuses()
    {
        var north = List("north", ["10.1.0.0/16"], users: false);
        var south = List("south", ["10.2.0.0/16"], users: false, drop: true);
        var open = List("open", satisfy: AccessListSatisfy.Any);
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [north, south, open],
            Hosts = [new HostSpec
            {
                Id = TestIds.Host("h"),
                Names = ["acl.test"],
                Policies = [new AccessListUnionPolicySpec([north.Id, south.Id])],
                Routes = [TestSnapshots.Route("root", "web")],
            }],
        };
        var gate = GateFor(AccessListFixtures.NewRuntime(), model, out _);

        Assert.Equal("pass", await RunAsync(gate, "10.1.0.5"));
        Assert.Equal("pass", await RunAsync(gate, "10.2.0.5"));

        // Dropped only if every list drops: the north list answers 403, so that is what the host chain answers.
        Assert.Equal("403", await RunAsync(gate, Outside));
    }

    [Fact]
    public async Task TheHostChainsAddressStageAndTheRoutesFullStageCountOneAllowedRequest()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var list = List("office", Allow, Deny);
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts = [new HostSpec
            {
                Id = Paduraksa.Tests.Shared.TestIds.Host("h"),
                Names = ["acl.test"],
                Policies = [new AccessListPolicySpec(list.Id, AccessListStage.AddressOnly)],
                Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
            }],
        };
        var gate = GateFor(runtime, model, out _);

        Assert.Equal("pass", await RunAsync(gate, Inside, Basic("alice", Password)));

        Assert.Equal(1, runtime.Stats.Allowed);
    }

    [Fact]
    public void ASimulationNeitherReadsNorWritesTheCache()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var snapshot = TestSnapshots.Bound(Model(List("office")), options: new SnapshotCompilerOptions { AccessLists = runtime });
        var policy = Assert.IsType<AccessListPolicy>(snapshot.HostsByIndex[0].Routes[0].Policies[0]);
        var context = Context(Outside, Basic("alice", Password));
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse(Outside), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        RequestView simulated = new(context, state, isSimulation: true);

        Assert.Equal(PolicyAction.Continue, policy.Evaluate(in simulated).Action);
        Assert.Equal(PolicyAction.Continue, policy.Evaluate(in simulated).Action);

        Assert.Equal(0, AccessListFixtures.Cached(runtime));
        Assert.Equal((0, 0, 0, 0, 0, 0), (runtime.Stats.Allowed, runtime.Stats.AddressRefused, runtime.Stats.Unauthorized, runtime.Stats.CacheHits, runtime.Stats.Verifications, runtime.Stats.Busy));
    }

    [Fact]
    public async Task EveryVerdictThatNeedsNoPbkdf2AllocatesNothing()
    {
        var runtime = AccessListFixtures.NewRuntime();
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var lan = List("lan", Allow, users: false);
        var quiet = List("quiet", Allow, users: false, drop: true);
        var passwords = List("passwords");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [lan, quiet, passwords],
            Hosts =
            [
                Host("lan.test", lan),
                Host("quiet.test", quiet),
                Host("passwords.test", passwords),
            ],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);

        // The one PBKDF2 a password costs, through the whole gate, so the measured requests find it remembered.
        Assert.Equal("pass", await RunAsync(new GateMiddleware(store, addresses), Context(Outside, Basic("alice", Password), "passwords.test")));

        var cases = new (string Name, DefaultHttpContext Context, GateAction Expected)[]
        {
            ("an allowed address", Context(Inside, host: "lan.test"), GateAction.Forward),
            ("a refused address (403)", Context(Outside, host: "lan.test"), GateAction.Respond),
            ("a refused address (dropped)", Context(Outside, host: "quiet.test"), GateAction.Abort),
            ("no credentials (401)", Context(Outside, host: "passwords.test"), GateAction.Respond),
            ("unreadable credentials (401)", Context(Outside, "Basic ???", "passwords.test"), GateAction.Respond),
            ("a remembered password", Context(Outside, Basic("alice", Password), "passwords.test"), GateAction.Forward),
            ("a password that is not remembered (the decision defers; nothing is checked yet)", Context(Outside, Basic("alice", "wrong"), "passwords.test"), GateAction.AwaitPolicy),
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
