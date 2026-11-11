using System.Net;
using System.Text;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Tls;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Integration.Proxy;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

public sealed class RequestSimulatorEndToEndTests
{
    private static readonly SimulationRequest Request = new("http://app.test/api/x", "203.0.113.7");

    [Fact]
    public async Task TheLiveSnapshotIsSimulatedWithItsUpstreamAndLiveHealth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        var simulator = host.Shared.Simulator;
        await CommitAsync(host, new UpsertUpstreamCommand(Upstream("app", "http://127.0.0.1:9/")), ct);

        // Before the host exists: the default host answers.
        var before = await simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);
        Assert.Equal(SimulationStatus.Simulated, before.Status);
        Assert.Equal(SimulationOutcomeKind.DefaultHost, before.Outcome!.Kind);
        Assert.Equal(404, before.Outcome.StatusCode);

        var revision = await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), ct);
        var live = await simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);

        Assert.Equal(SimulationStatus.Simulated, live.Status);
        Assert.Equal(new SimulationSource(SimulationTargetKind.Live, revision.Id), live.Source);
        Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, live.Outcome!.Kind);
        var upstream = live.Upstream!;
        Assert.Equal(TestIds.Upstream("app"), upstream.Id);
        var destination = Assert.Single(upstream.Destinations);
        Assert.Equal(TestIds.Destination("app-1"), destination.Id);
        Assert.Equal("http://127.0.0.1:9/", destination.Address);
        // The live proxy sends traffic to it (no check has failed): no destination is named as "the" one chosen.
        Assert.Equal(SimulationHealth.Healthy, destination.Health);
        Assert.Contains(live.Trace, s => s.Kind == SimulationStepKind.RouteMatch && s.Route == TestIds.Route("app-root"));
        Assert.Contains(live.Trace, s => s.Kind == SimulationStepKind.ClientAddress);
    }

    [Fact]
    public async Task ARevisionIsCompiledAndSimulatedWithoutBecomingLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertUpstreamCommand(Upstream("app", "http://127.0.0.1:9/")), ct);
        var withHost = await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), ct);
        var without = await CommitAsync(host, new DeleteHostCommand(TestIds.Host("app"), (await host.Shared.Config.GetCurrentAsync(ct)).Document.Hosts[0].Version), ct);
        var generation = host.Shared.SnapshotStore.Current.Generation;

        var old = await host.Shared.Simulator.SimulateAsync(new RevisionSimulationTarget(withHost.Id), Request, Origin, ct);
        var current = await host.Shared.Simulator.SimulateAsync(new RevisionSimulationTarget(without.Id), Request, Origin, ct);

        Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, old.Outcome!.Kind);
        Assert.Equal(new SimulationSource(SimulationTargetKind.Revision, withHost.Id), old.Source);
        Assert.Equal(SimulationOutcomeKind.DefaultHost, current.Outcome!.Kind);
        Assert.Equal(generation, host.Shared.SnapshotStore.Current.Generation);
        // Health is the live proxy's: the old revision's upstream still exists there with the same destination.
        Assert.Equal(SimulationHealth.Healthy, old.Upstream!.Destinations[0].Health);
        Assert.Contains(old.Notes, n => n.Contains("health", StringComparison.OrdinalIgnoreCase));

        var missing = await host.Shared.Simulator.SimulateAsync(new RevisionSimulationTarget(TestIds.Revision("nope")), Request, Origin, ct);
        Assert.Equal(SimulationStatus.RevisionNotFound, missing.Status);
        Assert.Null(missing.Outcome);
    }

    [Fact]
    public async Task ADraftDocumentIsSimulatedNotStoredNorPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        var before = await host.Shared.Config.GetCurrentAsync(ct);
        var generation = host.Shared.SnapshotStore.Current.Generation;
        var draft = before.Document with
        {
            Upstreams = [Upstream("app", "http://127.0.0.1:9/")],
            AccessLists = [new AccessListConfig(TestIds.AccessList("lan"), "lan", Ip: new AccessListIp(Allow: ["10.0.0.0/8"]))],
            Hosts = [ProxyHost("app", "app.test", "app") with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = TestIds.AccessList("lan") } } }],
        };

        var blocked = await host.Shared.Simulator.SimulateAsync(new DraftDocumentSimulationTarget(draft), Request, Origin, ct);
        var allowed = await host.Shared.Simulator.SimulateAsync(
            new DraftDocumentSimulationTarget(draft), Request with { ClientIp = "10.1.2.3" }, Origin, ct);

        Assert.Equal(SimulationOutcomeKind.FixedAnswer, blocked.Outcome!.Kind);
        Assert.Equal(403, blocked.Outcome.StatusCode);
        Assert.Contains(blocked.Trace, s => s.Kind == SimulationStepKind.HostPolicy && s.Verdict == SimulationStepVerdict.Stopped);
        Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, allowed.Outcome!.Kind);
        Assert.Equal(new SimulationSource(SimulationTargetKind.DraftDocument), allowed.Source);
        // The draft's upstream doesn't exist in the live proxy: its health is unknown, not guessed.
        Assert.Equal(SimulationHealth.Unknown, Assert.Single(allowed.Upstream!.Destinations).Health);

        // Nothing was stored or published, and the live config still answers as it did.
        Assert.Equal(before.Revision, (await host.Shared.Config.GetCurrentAsync(ct)).Revision);
        Assert.Equal(generation, host.Shared.SnapshotStore.Current.Generation);
        var live = await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);
        Assert.Equal(SimulationOutcomeKind.DefaultHost, live.Outcome!.Kind);
    }

    [Fact]
    public async Task ADraftCommandIsAppliedToTheCurrentConfigThroughTheDryRunPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertUpstreamCommand(Upstream("app", "http://127.0.0.1:9/")), ct);
        var revision = await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), ct);
        var current = (await host.Shared.Config.GetCurrentAsync(ct)).Document.Hosts[0];
        var generation = host.Shared.SnapshotStore.Current.Generation;

        // The pending change makes the host a Respond host: the live config forwards, the draft answers.
        var responding = current with { Kind = HostKind.Respond, Respond = new RespondConfig(503, "maintenance"), Routes = [] };
        var draft = await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(responding)), Request, Origin, ct);
        var live = await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);

        Assert.Equal(SimulationStatus.Simulated, draft.Status);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, draft.Outcome!.Kind);
        Assert.Equal(503, draft.Outcome.StatusCode);
        Assert.Equal("maintenance", draft.Outcome.Body);
        Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, live.Outcome!.Kind);

        // The draft's own DryRun flag changes nothing: still not stored.
        await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(responding) { DryRun = false }), Request, Origin, ct);
        Assert.Equal(revision, (await host.Shared.Config.GetCurrentAsync(ct)).Revision);
        Assert.Equal(generation, host.Shared.SnapshotStore.Current.Generation);

        // A command that changes nothing simulates the config as it is, and says so.
        var unchanged = await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(current)), Request, Origin, ct);
        Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, unchanged.Outcome!.Kind);
        Assert.Contains(unchanged.Notes, n => n.Contains("changes nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADraftThatFailsValidationExplainsWhyInsteadOfSimulating()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertUpstreamCommand(Upstream("app", "http://127.0.0.1:9/")), ct);
        await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), ct);

        // A second host taking the same name, and a route to an upstream that doesn't exist.
        var clash = ProxyHost("clash", "app.test", "nowhere");
        var result = await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(clash)), Request, Origin, ct);

        Assert.Equal(SimulationStatus.Invalid, result.Status);
        Assert.Null(result.Outcome);
        Assert.Empty(result.Trace);
        Assert.NotEmpty(result.Errors.Concat(result.RelatedErrors));
        Assert.All(result.Errors.Concat(result.RelatedErrors), e => Assert.NotEmpty(e.Code));

        // The same through a whole document.
        var current = await host.Shared.Config.GetCurrentAsync(ct);
        var document = await host.Shared.Simulator.SimulateAsync(
            new DraftDocumentSimulationTarget(current.Document with { Hosts = [.. current.Document.Hosts, clash] }), Request, Origin, ct);
        Assert.Equal(SimulationStatus.Invalid, document.Status);
        Assert.NotEmpty(document.Errors);

        // A stale version is not a validation error: the draft can't be applied to the config as it is.
        var stale = current.Document.Hosts[0] with { Version = current.Document.Hosts[0].Version + 5 };
        var conflict = await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(stale)), Request, Origin, ct);
        Assert.Equal(SimulationStatus.DraftRejected, conflict.Status);
        Assert.Single(conflict.Errors);

        // Deleting what doesn't exist.
        var missing = await host.Shared.Simulator.SimulateAsync(
            new DraftCommandSimulationTarget(new DeleteHostCommand(TestIds.Host("ghost"), 1)), Request, Origin, ct);
        Assert.Equal(SimulationStatus.DraftRejected, missing.Status);
    }

    [Fact]
    public async Task AChallengeIsAnsweredFromTheLiveStoreWhateverTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        SimulationRequest challenge = new("http://anything.test/.well-known/acme-challenge/tok-en_1", "203.0.113.7");
        SimulationTarget[] targets = [SimulationTarget.Live, new DraftDocumentSimulationTarget(new ConfigDocument())];

        // Unknown token: falls through to the default host.
        foreach (var target in targets)
        {
            Assert.Equal(SimulationOutcomeKind.DefaultHost, (await host.Shared.Simulator.SimulateAsync(target, challenge, Origin, ct)).Outcome!.Kind);
        }

        // Published (challenges are process state, not config): answered for every target.
        host.Shared.Challenges.Publish("tok-en_1", "tok-en_1.thumbprint");
        foreach (var target in targets)
        {
            var answered = await host.Shared.Simulator.SimulateAsync(target, challenge, Origin, ct);
            Assert.Equal(SimulationOutcomeKind.AcmeChallengeAnswer, answered.Outcome!.Kind);
            Assert.Equal("tok-en_1.thumbprint", answered.Outcome.Body);
        }
    }

    [Fact]
    public async Task AMalformedRequestIsReportedBeforeAnythingIsCompiled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);

        var result = await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, new SimulationRequest("nonsense", "300.1.1.1"), Origin, ct);

        Assert.Equal(SimulationStatus.BadRequest, result.Status);
        Assert.Equal(["/url", "/clientIp"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task TheDraftsInventoryNeverLoadsAKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertUpstreamCommand(Upstream("app", "http://127.0.0.1:9/")), ct);
        await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), ct);

        // HTTPS for a draft: the certificate table answers from placeholders, and the trace names the entry.
        var result = await host.Shared.Simulator.SimulateAsync(
            new DraftCommandSimulationTarget(new UpsertHostCommand(ProxyHost("other", "other.test", "app"))),
            new SimulationRequest("https://other.test/", "203.0.113.7"),
            Origin,
            ct);

        Assert.Equal(SimulationStatus.Simulated, result.Status);
        var handshake = Assert.Single(result.Trace, s => s.Kind == SimulationStepKind.TlsHandshake);
        Assert.NotEqual(SimulationStepVerdict.Skipped, handshake.Verdict);
        Assert.Contains(handshake.Facts, f => f.Name == "certificate"); // which inventory certificate would be served
        Assert.DoesNotContain(result.Trace.SelectMany(s => s.Facts), f => f.Value.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAuthorizerHookCanDenyAnySimulation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);

        // The shipped hook allows everything; a replaced one decides per target and origin.
        Assert.Equal(SimulationStatus.Simulated, (await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, CommandOrigin.Cli, ct)).Status);
        RequestSimulator guarded = new(host.Shared.Config, host.Shared.SnapshotStore, TestPermissions.Holding(Permission.Simulate));

        Assert.Equal(SimulationStatus.Simulated, (await guarded.SimulateAsync(SimulationTarget.Live, Request, Origin, ct)).Status);
        var denied = await guarded.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument()), Request, Origin, ct);
        Assert.Equal(SimulationStatus.Denied, denied.Status);
        Assert.Null(denied.Outcome);
    }

    [Fact]
    public async Task ACertificateTheVaultCantServeIsRefusedAsTheRealCommandRefusesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        var held = (await host.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["held.test"]), Origin, ct)).Certificate!.Id;
        var missing = TestIds.Certificate("missing");
        var stray = certificates.Create("missing.test", "missing.test");
        await host.Shared.CertificateService.RegisterAsync(new CertificateRegistration(missing, CertificateSource.Uploaded, PlaceholderCertificates.InfoOf(stray.Leaf)), Origin, ct);

        // Active in the inventory table, but neither the vault nor the process holds a key for it.
        var bound = RespondHost("bound", "bound.test", "x") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = missing } };
        var real = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(bound) { DryRun = true }, Origin, ct);
        var simulated = await host.Shared.Simulator.SimulateAsync(
            new DraftCommandSimulationTarget(new UpsertHostCommand(bound)), new SimulationRequest("https://bound.test/", "203.0.113.7"), Origin, ct);

        Assert.Equal(ConfigCommandStatus.Invalid, real.Status);
        Assert.Equal(SimulationStatus.Invalid, simulated.Status);
        Assert.Equal(real.Errors.Concat(real.RelatedErrors).Select(e => e.Code), simulated.Errors.Concat(simulated.RelatedErrors).Select(e => e.Code));

        // A certificate the vault holds is usable, and the trace names it for the handshake.
        var auto = RespondHost("auto", "held.test", "x");
        Assert.Equal(ConfigCommandStatus.DryRun, (await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(auto) { DryRun = true }, Origin, ct)).Status);
        var ok = await host.Shared.Simulator.SimulateAsync(
            new DraftCommandSimulationTarget(new UpsertHostCommand(auto)), new SimulationRequest("https://held.test/", "203.0.113.7"), Origin, ct);
        Assert.Equal(SimulationStatus.Simulated, ok.Status);
        Assert.Equal(held.ToString(), Assert.Single(ok.Trace, t => t.Kind == SimulationStepKind.TlsHandshake).Facts.Single(f => f.Name == "certificate").Value);
    }

    [Fact]
    public async Task APayloadWithNullElementsOrAnotherApiVersionIsInvalidNotACrash()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        var simulator = host.Shared.Simulator;

        var nullHost = await simulator.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument(Hosts: [null!])), Request, Origin, ct);
        var version = await simulator.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument(ApiVersion: "paduraksa.dev/v9")), Request, Origin, ct);
        var name = await simulator.SimulateAsync(
            new DraftCommandSimulationTarget(new UpsertHostCommand(RespondHost("n", "n.test", "x") with { Names = ["n.test", null!] })), Request, Origin, ct);

        Assert.Equal(SimulationStatus.Invalid, nullHost.Status);
        Assert.Equal("/hosts/0", Assert.Single(nullHost.Errors).Path);
        Assert.Equal(SimulationStatus.Invalid, version.Status);
        Assert.Equal("/apiVersion", Assert.Single(version.Errors).Path);
        Assert.Equal(SimulationStatus.Invalid, name.Status);
        Assert.Equal("/names/1", Assert.Single(name.Errors).Path);
    }

    [Fact]
    public async Task WhenTheCompileSlotsAreTakenADraftIsAnsweredUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        using RequestSimulator simulator = new(host.Shared.Config, host.Shared.SnapshotStore, TestPermissions.AllowAll, busyWait: TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < RequestSimulator.MaxConcurrentCompiles; i++)
        {
            await simulator.CompileSlots.WaitAsync(ct);
        }

        var draft = await simulator.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument()), Request, Origin, ct);
        var live = await simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);

        Assert.Equal(SimulationStatus.Unavailable, draft.Status);
        Assert.Equal(SimulationErrorCodes.Busy, Assert.Single(draft.Errors).Code);
        Assert.Equal(SimulationStatus.Simulated, live.Status); // the live target compiles nothing: no slot
        simulator.CompileSlots.Release(RequestSimulator.MaxConcurrentCompiles);
        Assert.Equal(SimulationStatus.Simulated, (await simulator.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument()), Request, Origin, ct)).Status);
    }

    [Fact]
    public async Task ACorruptHeadIsAnUnavailableResultNotAnException()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("a", "a.test", "A")), ct);
        var head = await CommitAsync(host, new UpsertHostCommand(RespondHost("b", "b.test", "B")), ct);

        await ExecuteSqlAsync(data, "UPDATE ConfigRevisions SET DocumentHash = zeroblob(32) WHERE Seq = 2", ct);

        var command = await host.Shared.Simulator.SimulateAsync(new DraftCommandSimulationTarget(new UpsertHostCommand(RespondHost("c", "c.test", "C"))), Request, Origin, ct);
        var document = await host.Shared.Simulator.SimulateAsync(new DraftDocumentSimulationTarget(new ConfigDocument()), Request, Origin, ct);
        var revision = await host.Shared.Simulator.SimulateAsync(new RevisionSimulationTarget(head.Id), Request, Origin, ct);

        Assert.Equal(SimulationStatus.Unavailable, command.Status);
        Assert.Equal(SimulationStatus.Unavailable, document.Status);
        Assert.Equal(SimulationStatus.Unavailable, revision.Status);
        Assert.All([command, document, revision], r => Assert.NotEmpty(r.Errors));
        Assert.Equal(SimulationStatus.Simulated, (await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct)).Status);
    }

    [Fact]
    public async Task APendingCommitDoesNotFailADraftDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        ConfigDocument draft = new(Hosts: [RespondHost("d", "d.test", "D")]);

        // Commits racing the simulations: a draft is planned on the head it reads, or on the new one, never "rejected".
        var commits = Task.Run(
            async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await CommitAsync(host, new UpsertHostCommand(RespondHost($"r{i}", $"r{i}.test", "R")), ct);
                }
            },
            ct);
        List<SimulationStatus> statuses = [];
        while (!commits.IsCompleted)
        {
            statuses.Add((await host.Shared.Simulator.SimulateAsync(new DraftDocumentSimulationTarget(draft), Request, Origin, ct)).Status);
        }

        await commits;
        Assert.DoesNotContain(SimulationStatus.DraftRejected, statuses);
    }

    [Fact]
    public async Task ThePublicListenersProxyProtocolRulesDecideWhetherAConnectionExists()
    {
        var ct = TestContext.Current.CancellationToken;
        Dictionary<string, string?> settings = new()
        {
            ["Paduraksa:Listen:HttpProxyProtocol:Enabled"] = "true",
            ["Paduraksa:Listen:HttpProxyProtocol:TrustedSources:0"] = "192.0.2.0/24",
        };
        await using var host = TestHost.Create(settings: settings);
        await host.StartAsync(ct);

        var none = await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, Origin, ct);
        var untrusted = await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request with { ProxyProtocol = true, PeerIp = "203.0.113.7" }, Origin, ct);
        var trusted = await host.Shared.Simulator.SimulateAsync(
            SimulationTarget.Live, Request with { ProxyProtocol = true, PeerIp = "203.0.113.7", TransportIp = "192.0.2.9" }, Origin, ct);

        Assert.Equal(SimulationOutcomeKind.ConnectionRejected, none.Outcome!.Kind);
        Assert.Equal(SimulationOutcomeKind.ConnectionRejected, untrusted.Outcome!.Kind);
        Assert.NotEqual(SimulationOutcomeKind.ConnectionRejected, trusted.Outcome!.Kind);
        Assert.Equal(SimulationStepKind.Connection, trusted.Trace[0].Kind);

        // Over a real socket the listener drops the header-less connection too.
        Assert.Equal(string.Empty, await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n")));
    }

    [Fact]
    public async Task TheServersIssuanceServicesRefuseAnOriginWithoutTheProvidersPermission()
    {
        // The issuance services are built in a container of their own: they must be given the same permission check as the rest.
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        var created = await host.Shared.Users.CreateAsync(Origin, new CreateUserRequest("operator", null, "correct-horse-battery-staple-9", [Role.Operator]), ct);
        Assert.True(created.Succeeded, created.Problem);
        CommandOrigin operatorOrigin = new(Actor.ForUser(created.User!.Id), Channel.Api, IPAddress.Loopback); // config and certificates, not providers

        var account = await host.Shared.AcmeAccounts.CreateAsync(
            new Paduraksa.Contracts.Issuance.CreateAcmeAccountRequest("https://ca.example/directory", [], TermsOfServiceAgreed: true), operatorOrigin, ct);

        Assert.Equal(Paduraksa.Contracts.Issuance.IssuanceChangeStatus.Denied, account.Status);
    }

    [Fact]
    public async Task TheServerAuthorizesSimulationsByPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        async Task<CommandOrigin> UserWithAsync(string name, Role role)
        {
            var created = await host.Shared.Users.CreateAsync(Origin, new CreateUserRequest(name, null, "correct-horse-battery-staple-9", [role]), ct);
            Assert.True(created.Succeeded, created.Problem);
            return new CommandOrigin(Actor.ForUser(created.User!.Id), Channel.Api, IPAddress.Loopback);
        }

        var viewer = await UserWithAsync("viewer", Role.Viewer); // simulate + config:read
        var auditor = await UserWithAsync("auditor", Role.Auditor); // config:read, no simulate
        SimulationTarget draft = new DraftDocumentSimulationTarget(new ConfigDocument());

        Assert.Equal(SimulationStatus.Simulated, (await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, viewer, ct)).Status);
        Assert.Equal(SimulationStatus.Simulated, (await host.Shared.Simulator.SimulateAsync(draft, Request, viewer, ct)).Status);
        Assert.Equal(SimulationStatus.Denied, (await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, auditor, ct)).Status);
        Assert.Equal(SimulationStatus.Denied, (await host.Shared.Simulator.SimulateAsync(draft, Request, auditor, ct)).Status);
        Assert.Equal(SimulationStatus.Denied, (await host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, Request, new CommandOrigin(Actor.ForUser(UserId.New()), Channel.Api), ct)).Status);
        Assert.Equal(SimulationStatus.Simulated, (await host.Shared.Simulator.SimulateAsync(draft, Request, CommandOrigin.Cli, ct)).Status);
    }
}
