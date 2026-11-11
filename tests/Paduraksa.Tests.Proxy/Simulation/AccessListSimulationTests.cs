using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.AccessLists;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// The simulator's access-list step: which list, what the address rules said, what the credentials said and how the list's
/// satisfy mode combined them, in words and as facts, and never the password. It runs the live policy, so what it reports is
/// what the real gate does (the parity tests prove that for every verdict), and it leaves the verification cache, the
/// counters and the concurrency limit exactly as they were.
/// </summary>
public sealed class AccessListSimulationTests
{
    private const string WrongPassword = "hunter2";
    private const string Inside = "10.1.1.1";
    private const string Outside = "192.0.2.1";
    private const string Vetoed = "10.9.1.1";

    private static readonly string[] Allow = ["10.0.0.0/8"];
    private static readonly string[] Deny = ["10.9.0.0/16"];

    public static TheoryData<AccessListSatisfy, string, string> Matrix
    {
        get
        {
            TheoryData<AccessListSatisfy, string, string> rows = [];
            foreach (var satisfy in new[] { AccessListSatisfy.All, AccessListSatisfy.Any })
            {
                foreach (var client in new[] { Inside, Outside, Vetoed })
                {
                    foreach (var credentials in new[] { "none", "bad", "unknown", "good", "garbage" })
                    {
                        rows.Add(satisfy, client, credentials);
                    }
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task TheSimulatedOutcomeIsWhatTheRealGateDoes(AccessListSatisfy satisfy, string client, string credentials)
    {
        var (snapshot, gate) = Build(satisfy, out _);
        var header = Header(credentials);

        var simulated = GateSimulator.Run(snapshot, Probe(client, header)).Outcome;
        var real = await RunAsync(gate, client, header);

        Assert.Equal(real, simulated.Action switch
        {
            GateOutcomeAction.Forward => "pass",
            GateOutcomeAction.Abort => "drop",
            _ => simulated.StatusCode.ToString(),
        });
    }

    [Theory]
    [InlineData(Inside, "none", "allowed", "missing")]
    [InlineData(Inside, "bad", "allowed", "wrongPassword")]
    [InlineData(Inside, "unknown", "allowed", "unknownUser")]
    [InlineData(Inside, "good", "allowed", "ok")]
    [InlineData(Inside, "garbage", "allowed", "unreadable")]
    [InlineData(Outside, "good", "notInAllowList", "notAsked")]
    [InlineData(Vetoed, "good", "denied", "notAsked")]
    public void TheStepReportsTheAddressAndTheCredentialsVerdicts(string client, string credentials, string address, string expected)
    {
        var (snapshot, _) = Build(AccessListSatisfy.All, out _);

        var step = AccessListStep(GateSimulator.Run(snapshot, Probe(client, Header(credentials))));

        Assert.Equal("accessList", Fact(step, "policy"));
        Assert.Equal("office", Fact(step, "accessList"));
        Assert.Equal(address, Fact(step, "address"));
        Assert.Equal(expected, Fact(step, "credentials"));
        Assert.Equal("all", Fact(step, "satisfy"));
    }

    [Fact]
    public void TheSentenceNamesTheListTheMatchingEntryAndHowSatisfyCombinedThem()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All, out _);

        Assert.Equal(
            "accessList: access list 'office': client 10.1.1.1 is in the allow list (10.0.0.0/8); the password is right (satisfy address and password): lets the request through",
            AccessListStep(GateSimulator.Run(snapshot, Probe(Inside, Header("good")))).Summary);
        Assert.Equal(
            "accessList: access list 'office': client 10.9.1.1 is denied by 10.9.0.0/16; no password is asked of a refused address (satisfy address and password): answers 403",
            AccessListStep(GateSimulator.Run(snapshot, Probe(Vetoed, null))).Summary);
        Assert.Equal(
            "accessList: access list 'office': client 192.0.2.1 is not in the allow list (1 entries); no password is asked of a refused address (satisfy address and password): answers 403",
            AccessListStep(GateSimulator.Run(snapshot, Probe(Outside, null))).Summary);

        var (anySnapshot, _) = Build(AccessListSatisfy.Any, out _);
        Assert.Equal(
            "accessList: access list 'office': client 192.0.2.1 is not in the allow list (1 entries); no credentials were sent (satisfy address or password): answers 401",
            AccessListStep(GateSimulator.Run(anySnapshot, Probe(Outside, null))).Summary);
        Assert.Equal(
            "accessList: access list 'office': client 10.1.1.1 is in the allow list (10.0.0.0/8); the address passes without a password (satisfy address or password): lets the request through",
            AccessListStep(GateSimulator.Run(anySnapshot, Probe(Inside, null))).Summary);
    }

    [Fact]
    public void NeitherThePasswordNorTheTypedNameIsEverEchoed()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All, out _);

        foreach (var credentials in new[] { "none", "bad", "unknown", "good", "garbage" })
        {
            var run = GateSimulator.Run(snapshot, Probe(Inside, Header(credentials)));
            var everything = string.Join('\n', run.Steps.SelectMany(s => s.Facts.Select(f => $"{f.Name}={f.Value}").Append(s.Summary)).Append(run.Outcome.Body ?? string.Empty));

            Assert.DoesNotContain(Password, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(WrongPassword, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("mallory", everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Header(credentials) ?? "no header", everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String("alice:"u8), everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASimulationNeitherCachesNorCountsNorTakesAVerificationSlot()
    {
        var (snapshot, gate) = Build(AccessListSatisfy.All, out var runtime);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal((GateOutcomeAction.Forward, null), (GateSimulator.Run(snapshot, Probe(Inside, Header("good"))).Outcome.Action, (int?)null));
            Assert.Equal(401, GateSimulator.Run(snapshot, Probe(Inside, Header("bad"))).Outcome.StatusCode);
        }

        Assert.Equal(0, runtime.Cache.LiveCount(runtime.NowMs));
        Assert.Equal((0, 0, 0, 0, 0, 0), (runtime.Stats.Allowed, runtime.Stats.AddressRefused, runtime.Stats.Unauthorized, runtime.Stats.CacheHits, runtime.Stats.Verifications, runtime.Stats.Busy));

        // The real gate then pays its own PBKDF2 once and remembers it: the simulations left it nothing to hit.
        Assert.Equal("pass", await RunAsync(gate, Inside, Header("good")));
        Assert.Equal("pass", await RunAsync(gate, Inside, Header("good")));
        Assert.Equal((1, 1), (runtime.Stats.Verifications, runtime.Stats.CacheHits));
    }

    [Fact]
    public async Task ASimulationWorksWhileEveryVerificationSlotIsTaken()
    {
        var busy = new AccessListRuntime(TimeProvider.System, maxConcurrentVerifications: 1);
        Assert.Equal(VerificationSlot.Granted, await busy.Queue.EnterAsync(IpKey.Parse("203.0.113.200"), CancellationToken.None));
        var crowded = TestSnapshots.Bound(AccessListFixtures.Model(List("office", Allow, Deny)), options: new SnapshotCompilerOptions { AccessLists = busy });

        Assert.Equal(GateOutcomeAction.Forward, GateSimulator.Run(crowded, Probe(Inside, Header("good"))).Outcome.Action);
        Assert.Equal((0, 0), (busy.Stats.Busy, busy.Stats.Queued));
    }

    [Fact]
    public void TheHostChainStepSaysWhyNoPasswordIsAskedThereYet()
    {
        var list = List("office", Allow, Deny);
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts = [new HostSpec
            {
                Id = TestIds_Host(),
                Names = ["acl.test"],
                Policies = [new AccessListPolicySpec(list.Id, AccessListStage.AddressOnly)],
                Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
            }],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = new AccessListRuntime() });

        var run = GateSimulator.Run(snapshot, Probe(Inside, null));

        var hostStep = Assert.Single(run.Steps, s => s.Kind == GateStepKind.HostPolicy);
        Assert.Contains("passwords are asked on the route, after the HTTPS redirect", hostStep.Summary, StringComparison.Ordinal);
        Assert.Equal("notAsked", Fact(hostStep, "credentials"));
        var routeStep = Assert.Single(run.Steps, s => s.Kind == GateStepKind.RoutePolicy);
        Assert.Equal("missing", Fact(routeStep, "credentials"));
        Assert.Equal(401, run.Outcome.StatusCode);
    }

    [Fact]
    public void TheUnionStepNamesTheListsAndSaysWhetherAnyRouteAdmitsTheAddress()
    {
        var north = List("north", ["10.1.0.0/16"], users: false);
        var south = List("south", ["10.2.0.0/16"], users: false);
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [north, south],
            Hosts = [new HostSpec
            {
                Id = TestIds_Host(),
                Names = ["acl.test"],
                Policies = [new AccessListUnionPolicySpec([north.Id, south.Id])],
                Routes = [TestSnapshots.Route("root", "web")],
            }],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = new AccessListRuntime() });

        var admitted = AccessListStep(GateSimulator.Run(snapshot, Probe("10.2.0.5", null)));
        var refused = AccessListStep(GateSimulator.Run(snapshot, Probe(Outside, null)));

        Assert.Equal("north, south", Fact(admitted, "accessList"));
        Assert.Equal(("allowed", "notAsked"), (Fact(admitted, "address"), Fact(admitted, "credentials")));
        Assert.Equal("refused", Fact(refused, "address"));
    }

    private static Paduraksa.Core.Model.HostId TestIds_Host() => Paduraksa.Tests.Shared.TestIds.Host("acl.test");

    private static (RuntimeSnapshot Snapshot, GateMiddleware Gate) Build(AccessListSatisfy satisfy, out AccessListRuntime runtime)
    {
        runtime = new AccessListRuntime();
        var model = AccessListFixtures.Model(List("office", Allow, Deny, satisfy: satisfy));
        var gate = GateFor(runtime, model, out var snapshot);
        return (snapshot, gate);
    }

    private static string? Header(string credentials) => credentials switch
    {
        "good" => Basic("alice", Password),
        "bad" => Basic("alice", WrongPassword),
        "unknown" => Basic("mallory", Password),
        "garbage" => "Basic ???",
        _ => null,
    };

    private static GateProbe Probe(string client, string? authorization) => new()
    {
        Host = "acl.test",
        Path = "/",
        ClientAddress = IPAddress.Parse(client),
        Headers = authorization is null ? [] : [new ProbeHeader("Authorization", authorization)],
    };

    private static GateStep AccessListStep(GateSimulation run) =>
        Assert.Single(run.Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "accessList" }));

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;
}
