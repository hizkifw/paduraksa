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
    public void SatisfyCombinesTheAddressAndThePassword(AccessListSatisfy satisfy, string client, string credentials, string expected)
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("office", Allow, Deny, satisfy: satisfy)), out _);
        var authorization = credentials switch
        {
            "good" => Basic("alice", Password),
            "bad" => Basic("alice", "wrong"),
            _ => null,
        };

        var decision = Decide(gate, client, authorization);

        Assert.Equal(expected, Describe(decision));
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
        var snapshot = TestSnapshots.Bound(Model(list), options: new SnapshotCompilerOptions { AccessLists = new AccessListRuntime() });
        var policy = snapshot.HostsByIndex[0].Routes[0].Policies[0];
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse(client), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        var request = new RequestView(Context("192.0.2.1"), state);

        Assert.Equal(allowed ? PolicyAction.Continue : PolicyAction.Respond, policy.Evaluate(in request).Action);
    }

    [Fact]
    public void WithoutUsersOnlyTheAddressApplies()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("lan", Allow, Deny, users: false)), out _);

        Assert.Equal("pass", Describe(Decide(gate, Inside)));
        Assert.Equal("403", Describe(Decide(gate, Outside)));
        Assert.Equal("403", Describe(Decide(gate, Vetoed)));
    }

    [Fact]
    public void WithoutAddressRulesOnlyThePasswordApplies()
    {
        foreach (var satisfy in new[] { AccessListSatisfy.All, AccessListSatisfy.Any })
        {
            var gate = GateFor(new AccessListRuntime(), Model(List("open", satisfy: satisfy)), out _);

            Assert.Equal("401", Describe(Decide(gate, Outside)));
            Assert.Equal("pass", Describe(Decide(gate, Outside, Basic("bob", "battery staple"))));
        }
    }

    [Fact]
    public void ADenyEntryAloneRefusesThatAddressAndAsksEveryoneElseForAPassword()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("veto", deny: Deny, satisfy: AccessListSatisfy.Any)), out _);

        Assert.Equal("403", Describe(Decide(gate, Vetoed, Basic("alice", Password))));
        Assert.Equal("401", Describe(Decide(gate, Outside)));
        Assert.Equal("pass", Describe(Decide(gate, Outside, Basic("alice", Password))));
    }

    [Fact]
    public void DropDeniedAbortsAnAddressTheListRefusesButStillAsksForAPassword()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("quiet", Allow, Deny, drop: true)), out _);

        Assert.Equal("drop", Describe(Decide(gate, Outside)));
        Assert.Equal("drop", Describe(Decide(gate, Vetoed, Basic("alice", Password))));
        Assert.Equal("401", Describe(Decide(gate, Inside)));
        Assert.Equal("pass", Describe(Decide(gate, Inside, Basic("alice", Password))));
    }

    [Fact]
    public void TheChallengeIsPrecomputedWithTheRealmAndNoStore()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("Staff \"area\"", realm: "Staff \"area\"")), out _);

        var response = Decide(gate, Outside).Response!;

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Basic realm=\"Staff \\\"area\\\"\", charset=\"UTF-8\"", Assert.Single(response.Headers, h => h.Name == "WWW-Authenticate").Value.ToString());
        Assert.Equal("no-store", response.CacheControl.ToString());
        Assert.NotNull(response.HtmlPage);
    }

    [Fact]
    public void ABackslashInTheRealmIsEscapedInTheChallenge()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("slashes", realm: "a\\b")), out _);

        var response = Decide(gate, Outside).Response!;

        Assert.Equal("Basic realm=\"a\\\\b\", charset=\"UTF-8\"", Assert.Single(response.Headers, h => h.Name == "WWW-Authenticate").Value.ToString());
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Basic")]
    [InlineData("Basic !!!not-base64")]
    [InlineData("Basic YWxpY2U=")]
    [InlineData("Basic Om5vLW5hbWU=")]
    public void CredentialsThatCannotBeReadAreNotAPassword(string header)
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        Assert.Equal("401", Describe(Decide(gate, Outside, header)));
        Assert.Equal(0, runtime.Stats.Verifications);
    }

    [Fact]
    public void AnotherHeaderLineIsNotAPasswordEither()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("office")), out _);
        var context = Context(Outside);
        context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues([Basic("alice", Password), Basic("alice", Password)]);

        Assert.Equal("401", Describe(gate.Decide(context)));
    }

    [Fact]
    public void ThePasswordMayContainColonsAndNonAsciiCharacters()
    {
        var hash = Paduraksa.Core.Security.PasswordHash.Create("pa:ss:wörd");
        AccessListSpec list = List("office") with { Users = [new AccessListUserSpec("carol", hash)] };
        var gate = GateFor(new AccessListRuntime(), Model(list), out _);

        Assert.Equal("pass", Describe(Decide(gate, Outside, Basic("carol", "pa:ss:wörd"))));
        Assert.Equal("401", Describe(Decide(gate, Outside, Basic("carol", "pa"))));
    }

    [Fact]
    public void NamesAreCaseSensitive()
    {
        var gate = GateFor(new AccessListRuntime(), Model(List("office")), out _);

        Assert.Equal("401", Describe(Decide(gate, Outside, Basic("Alice", Password))));
    }

    [Fact]
    public void AVerifiedPasswordIsRememberedAndNeedsNoSecondPbkdf2()
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        Assert.Equal("pass", Describe(Decide(gate, Outside, credentials)));
        Assert.Equal("pass", Describe(Decide(gate, Outside, credentials)));
        Assert.Equal("pass", Describe(Decide(gate, Outside, credentials)));

        Assert.Equal(1, runtime.Stats.Verifications);
        Assert.Equal(2, runtime.Stats.CacheHits);
    }

    [Fact]
    public void ARememberedPasswordExpiresAfterFiveMinutes()
    {
        StepClock clock = new();
        var runtime = new AccessListRuntime(clock);
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        Decide(gate, Outside, credentials);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Decide(gate, Outside, credentials);
        Assert.Equal(1, runtime.Stats.Verifications);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("pass", Describe(Decide(gate, Outside, credentials)));
        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public void AWrongPasswordIsNeverRemembered()
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("401", Describe(Decide(gate, Outside, Basic("alice", "wrong"))));
        }

        Assert.Equal(3, runtime.Stats.Verifications);
        Assert.Equal(0, runtime.Stats.CacheHits);
        Assert.Equal(0, runtime.Cache.LiveCount(runtime.NowMs));
    }

    [Fact]
    public void AnUnknownNameCostsAVerificationToo()
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        Assert.Equal("401", Describe(Decide(gate, Outside, Basic("mallory", Password))));

        Assert.Equal(1, runtime.Stats.Verifications);
    }

    [Fact]
    public void AnotherAddressDoesNotShareARememberedPassword()
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        Decide(gate, "192.0.2.1", credentials);
        Decide(gate, "192.0.2.2", credentials);

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public void AnIpv6ClientIsRememberedByItsSlash64()
    {
        var runtime = new AccessListRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);

        Decide(gate, "2001:db8:1:2::1", credentials);
        Decide(gate, "2001:db8:1:2::ffff", credentials);
        Decide(gate, "2001:db8:1:3::1", credentials);

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public void AChangedPasswordDoesNotReuseAnEntryOfTheOldOne()
    {
        var runtime = new AccessListRuntime();
        var before = GateFor(runtime, Model(List("office")), out _);
        var credentials = Basic("alice", Password);
        Assert.Equal("pass", Describe(Decide(before, Outside, credentials)));

        // The same list and user with a new password: a new hash and salt, so a new fingerprint.
        AccessListSpec changed = List("office") with { Users = [new AccessListUserSpec("alice", Paduraksa.Core.Security.PasswordHash.Create("a new password"))] };
        var after = GateFor(runtime, Model(changed), out _);

        Assert.Equal("401", Describe(Decide(after, Outside, credentials)));
        Assert.Equal("pass", Describe(Decide(after, Outside, Basic("alice", "a new password"))));
    }

    [Fact]
    public void TwoListsWithTheSameUserDoNotShareAnEntry()
    {
        var runtime = new AccessListRuntime();
        var one = List("one");
        var two = List("two");
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

        Decide(gate, Outside, credentials, "a.test");
        Decide(gate, Outside, credentials, "b.test");

        Assert.Equal(2, runtime.Stats.Verifications);
    }

    [Fact]
    public void TheCredentialsAreStrippedFromWhatTheUpstreamGetsOnlyWhenTheListConsumedThem()
    {
        foreach (var (pass, expected) in new[] { (false, true), (true, false) })
        {
            var gate = GateFor(new AccessListRuntime(), Model(List("office", passUpstream: pass)), out _);
            var context = Context(Outside, Basic("alice", Password));

            Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
            Assert.Equal(expected, context.Features.Get<IGateFeature>()!.State.StripAuthorization);
        }

        // A list that let the address in without asking never consumed a header the upstream may need.
        var lan = GateFor(new AccessListRuntime(), Model(List("lan", Allow, satisfy: AccessListSatisfy.Any)), out _);
        var ignored = Context(Inside, "Bearer for-the-upstream");
        Assert.Equal(GateAction.Forward, lan.Decide(ignored).Action);
        Assert.False(ignored.Features.Get<IGateFeature>()!.State.StripAuthorization);
    }

    [Fact]
    public void AHostChainListOnlyRefusesByAddressAndLeavesPasswordsToTheRoute()
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
        var gate = GateFor(new AccessListRuntime(), model, out _);

        Assert.Equal("403", Describe(Decide(gate, Outside)));
        Assert.Equal("pass", Describe(Decide(gate, Inside)));
    }

    [Fact]
    public void TheHostChainUnionRefusesOnlyWhatEveryRoutesListRefuses()
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
        var gate = GateFor(new AccessListRuntime(), model, out _);

        Assert.Equal("pass", Describe(Decide(gate, "10.1.0.5")));
        Assert.Equal("pass", Describe(Decide(gate, "10.2.0.5")));

        // Dropped only if every list drops: the north list answers 403, so that is what the host chain answers.
        Assert.Equal("403", Describe(Decide(gate, Outside)));
    }

    private static readonly IpKey Sprayer = IpKey.Parse("198.51.100.66");
    private static readonly IpKey Member = IpKey.Parse("203.0.113.9");

    [Fact]
    public void OnlyAsManyVerificationsRunAtOnceAsTheRuntimeAllows()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 2);

        Assert.True(runtime.TryEnterVerification(Sprayer));
        Assert.True(runtime.TryEnterVerification(Member));
        Assert.False(runtime.TryEnterVerification(IpKey.Parse("192.0.2.77")));
        runtime.ExitVerification(Member);
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("192.0.2.77")));
    }

    [Fact]
    public void WhileTheSystemIsIdleOneClientIsAdmittedWhateverItsOwnCount()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 8);

        // Four at once is more than the share of two, but fewer than half of the eight.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(runtime.TryEnterVerification(Sprayer));
        }
    }

    [Fact]
    public void FourColdLoginsFromOneClientAllSucceedWhenNothingElseIsRunning()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 8);
        var gate = GateFor(runtime, Model(List("office")), out _);
        var results = new string[4];
        using var start = new Barrier(results.Length);

        // The fourth request of an app that reopens after the cache expired, with the other three still hashing.
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i =>
        {
            start.SignalAndWait();
            results[i] = Describe(Decide(gate, Sprayer.ToString(), Basic("alice", Password)));
        });

        Assert.All(results, result => Assert.Equal("pass", result));
        Assert.Equal(0, runtime.Stats.Busy);
    }

    [Fact]
    public void FromHalfTheCapOneClientCanHaveOnlyItsOwnShareRunningAndTheRestIsLeftToOthers()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 8);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(runtime.TryEnterVerification(Sprayer));
        }

        Assert.False(runtime.TryEnterVerification(Sprayer));

        // Others are not affected, and a refusal gave back nothing it hadn't taken: the global count is still 7.
        Assert.True(runtime.TryEnterVerification(Member));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("192.0.2.1")));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("192.0.2.2")));
        runtime.ExitVerification(Sprayer);
        runtime.ExitVerification(Sprayer);
        Assert.False(runtime.TryEnterVerification(Sprayer));
        runtime.ExitVerification(Sprayer);
        Assert.True(runtime.TryEnterVerification(Sprayer));
    }

    [Fact]
    public void TheGlobalCapIsStillTheHardBoundForClientsWithinTheirShare()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 4);
        for (byte i = 1; i <= 4; i++)
        {
            Assert.True(runtime.TryEnterVerification(IpKey.Parse($"192.0.2.{i}")));
        }

        Assert.False(runtime.TryEnterVerification(Member));
    }

    [Fact]
    public void AnIpv6ClientSharesItsShareWithItsWholeSlash64()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 8);

        Assert.True(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:2::1")));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:2::ffff")));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:2:1::1")));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:2:2::1")));
        Assert.False(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:2:3::1")));
        Assert.True(runtime.TryEnterVerification(IpKey.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void ASprayerSaturatingTheSystemDoesNotBlockAnotherClientsVerification()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 8);
        var gate = GateFor(runtime, Model(List("office")), out _);
        var held = 0;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            held += runtime.TryEnterVerification(Sprayer) ? 1 : 0;
        }

        // It got half the cap, and no more however hard it kept trying.
        Assert.Equal(4, held);

        var refused = Decide(gate, Sprayer.ToString(), Basic("alice", "wrong"));
        var other = Decide(gate, Member.ToString(), Basic("alice", Password));

        Assert.Equal(429, refused.Response!.StatusCode);
        Assert.Equal("pass", Describe(other));
        Assert.Equal(1, runtime.Stats.Busy);

        // What the refused request took, it gave back.
        for (var i = 0; i < 3; i++)
        {
            runtime.ExitVerification(Sprayer);
        }

        Assert.True(runtime.TryEnterVerification(Sprayer));
    }

    [Fact]
    public void ASlotIsGivenBackWhateverTheVerificationDoes()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 1, maxVerificationsPerClient: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("401", Describe(Decide(gate, Sprayer.ToString(), Basic("alice", "wrong"))));
            Assert.Equal("401", Describe(Decide(gate, Sprayer.ToString(), Basic("nobody", "wrong"))));
        }

        Assert.Equal(0, runtime.Stats.Busy);
        Assert.True(runtime.TryEnterVerification(Sprayer));
    }

    [Fact]
    public void TakingAndGivingBackASlotAllocatesNothing()
    {
        var runtime = new AccessListRuntime();

        AllocAssert.Zero(runtime, static r =>
        {
            if (r.TryEnterVerification(Sprayer))
            {
                r.ExitVerification(Sprayer);
            }
        }, "a verification slot");
    }

    [Fact]
    public void WhenEveryVerificationSlotIsTakenAWrongPasswordIsAskedToWait()
    {
        var runtime = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        Assert.True(runtime.TryEnterVerification(Member));

        var decision = Decide(gate, Outside, Basic("alice", Password));

        Assert.Equal(429, decision.Response!.StatusCode);
        Assert.Equal("1", decision.RetryAfter);
        Assert.Equal(1, runtime.Stats.Busy);

        // A password that is already remembered needs no slot, so it still gets through.
        runtime.ExitVerification(Member);
        Assert.Equal("pass", Describe(Decide(gate, Outside, Basic("alice", Password))));
        Assert.True(runtime.TryEnterVerification(Member));
        Assert.Equal("pass", Describe(Decide(gate, Outside, Basic("alice", Password))));
    }

    [Fact]
    public void TheHostChainsAddressStageAndTheRoutesFullStageCountOneAllowedRequest()
    {
        var runtime = new AccessListRuntime();
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

        Assert.Equal("pass", Describe(Decide(gate, Inside, Basic("alice", Password))));

        Assert.Equal(1, runtime.Stats.Allowed);
    }

    [Fact]
    public void ASimulationNeitherReadsNorWritesTheCache()
    {
        var runtime = new AccessListRuntime();
        var snapshot = TestSnapshots.Bound(Model(List("office")), options: new SnapshotCompilerOptions { AccessLists = runtime });
        var policy = Assert.IsType<AccessListPolicy>(snapshot.HostsByIndex[0].Routes[0].Policies[0]);
        var context = Context(Outside, Basic("alice", Password));
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse(Outside), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        RequestView simulated = new(context, state, isSimulation: true);

        Assert.Equal(PolicyAction.Continue, policy.Evaluate(in simulated).Action);
        Assert.Equal(PolicyAction.Continue, policy.Evaluate(in simulated).Action);

        Assert.Equal(0, runtime.Cache.LiveCount(runtime.NowMs));
        Assert.Equal(2, runtime.Stats.Verifications);
        Assert.Equal(0, runtime.Stats.CacheHits);
    }

    [Fact]
    public void EveryVerdictThatNeedsNoPbkdf2AllocatesNothing()
    {
        var runtime = new AccessListRuntime();
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

        var cases = new (string Name, DefaultHttpContext Context, GateAction Expected)[]
        {
            ("an allowed address", Context(Inside, host: "lan.test"), GateAction.Forward),
            ("a refused address (403)", Context(Outside, host: "lan.test"), GateAction.Respond),
            ("a refused address (dropped)", Context(Outside, host: "quiet.test"), GateAction.Abort),
            ("no credentials (401)", Context(Outside, host: "passwords.test"), GateAction.Respond),
            ("unreadable credentials (401)", Context(Outside, "Basic ???", "passwords.test"), GateAction.Respond),
            ("a remembered password", Context(Outside, Basic("alice", Password), "passwords.test"), GateAction.Forward),
        };
        foreach (var (name, context, expected) in cases)
        {
            // The first request of a remembered password pays its PBKDF2; the measured ones must not.
            Assert.Equal(expected, GateMiddleware.Decide(context, snapshot, addresses, null, default(NoGateTracer)).Action);
            AllocAssert.Zero((snapshot, addresses, context), static s => _ = GateMiddleware.Decide(s.context, s.snapshot, s.addresses, null, default(NoGateTracer)), name);
        }
    }

    [Fact]
    public void AMissCostsOnePbkdf2AndAtMostAFewHundredBytes()
    {
        var runtime = new AccessListRuntime();
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var passwords = List("passwords");
        var snapshot = TestSnapshots.Bound(Model(passwords, "passwords.test"), options: new SnapshotCompilerOptions { AccessLists = runtime });
        var context = Context(Outside, Basic("alice", "wrong"), "passwords.test");

        var measured = AllocAssert.Measure((snapshot, addresses, context), static s => _ = GateMiddleware.Decide(s.context, s.snapshot, s.addresses, null, default(NoGateTracer)), warmup: 3, iterations: 20);

        Assert.True(measured.BytesPerCall < 512, $"{measured.BytesPerCall} B per wrong-password check");
    }

    private static HostSpec Host(string name, AccessListSpec list) => new()
    {
        Id = TestIds.Host(name),
        Names = [name],
        Routes = [TestSnapshots.Route(name, "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
    };

    private static string Describe(GateDecision decision) => decision.Action switch
    {
        GateAction.Forward => "pass",
        GateAction.Abort => "drop",
        GateAction.Respond => decision.Response!.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => decision.Action.ToString(),
    };
}
