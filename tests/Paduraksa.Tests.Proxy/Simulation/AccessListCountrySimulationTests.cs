using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.AccessLists;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>The simulator runs the live policy on a request whose country comes from the probe, so it must answer what the real gate
/// answers for a trusted Cloudflare peer's header, a forged one, and no header at all.</summary>
public sealed class AccessListCountrySimulationTests
{
    private const string Client = "192.0.2.1";

    private static readonly string[] AllowedCountries = ["DE", "XX"];
    private static readonly string[] DeniedCountries = ["RU", "T1"];

    // How the country reaches the gate: through Cloudflare (the probe adds the header), as a header a direct client forged, or not at all.
    public static TheoryData<string, string?, string> Requests
    {
        get
        {
            TheoryData<string, string?, string> rows = [];
            foreach (var how in new[] { "cloudflare", "forged", "none" })
            {
                foreach (var country in new[] { "DE", "de", "FR", "RU", "T1", "XX", "garbage", null })
                {
                    foreach (var satisfy in new[] { "all", "any" })
                    {
                        rows.Add(how, country, satisfy);
                    }
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task TheSimulatedOutcomeIsWhatTheRealGateDoes(string how, string? country, string satisfy)
    {
        var (snapshot, gate) = Build(satisfy == "any" ? AccessListSatisfy.Any : AccessListSatisfy.All);

        var simulated = GateSimulator.Run(snapshot, Probe(how, country)).Outcome;
        var real = await RunAsync(gate, how == "cloudflare" ? ViaCloudflare(Client, country) : Forged(country, how));

        Assert.Equal(real, simulated.Action switch
        {
            GateOutcomeAction.Forward => "pass",
            GateOutcomeAction.Abort => "drop",
            _ => simulated.StatusCode.ToString(),
        });
    }

    [Theory]
    [InlineData("DE", "allowed", "allowed", "Germany (DE) is an allowed country")]
    [InlineData("FR", "notInAllowList", "notInAllowList", "France (FR) is not in the allowed countries (2)")]
    [InlineData("RU", "denied", "denied", "Russia (RU) is a denied country")]
    [InlineData("T1", "denied", "denied", "Tor exit nodes (T1) is a denied country")]
    [InlineData("XX", "allowed", "allowed", "Unknown country (XX) is an allowed country")]
    [InlineData(null, "allowed", "allowed", "Unknown country (XX) is an allowed country")]
    public void TheStepNamesTheCountryAndTheRuleThatMatched(string? country, string address, string rule, string sentence)
    {
        var (snapshot, _) = Build(AccessListSatisfy.All);

        var step = AccessListStep(GateSimulator.Run(snapshot, Probe("cloudflare", country)));

        Assert.Equal(country is null ? "XX" : country, Fact(step, "country"));
        Assert.Equal(address, Fact(step, "address"));
        Assert.Equal(rule, Fact(step, "countryRule"));
        Assert.Contains(sentence, step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AForgedHeaderIsShownAsIgnoredInTheClientStep()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All);

        var run = GateSimulator.Run(snapshot, Probe("forged", "DE"));

        var client = Assert.Single(run.Steps, s => s.Kind == GateStepKind.ClientAddress);
        Assert.Equal("DE", Fact(client, "countryHeader"));
        Assert.Equal("ignored", Fact(client, "country"));
        Assert.Equal("allowed", Fact(AccessListStep(run), "address"));
        Assert.Equal("XX", Fact(AccessListStep(run), "country"));
    }

    [Fact]
    public void ATrustedPeersCountryIsShownInTheClientStep()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All);

        var client = Assert.Single(GateSimulator.Run(snapshot, Probe("cloudflare", "de")).Steps, s => s.Kind == GateStepKind.ClientAddress);

        Assert.Equal("de", Fact(client, "countryHeader"));
        Assert.Equal("DE", Fact(client, "country"));
    }

    [Fact]
    public void AHeaderTheOperatorSendsWinsOverTheProbesCountry()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All);
        var probe = Probe("cloudflare", "DE") with { Headers = [new ProbeHeader("CF-IPCountry", "RU")] };

        var client = Assert.Single(GateSimulator.Run(snapshot, probe).Steps, s => s.Kind == GateStepKind.ClientAddress);

        Assert.Equal("RU", Fact(client, "country"));
    }

    [Fact]
    public void AListWithoutCountryRulesAddsNoCountryFacts()
    {
        var snapshot = TestSnapshotsFor(List("plain", ["192.0.2.0/24"], users: false));

        var step = AccessListStep(GateSimulator.Run(snapshot, Probe("cloudflare", "DE")));

        Assert.DoesNotContain(step.Facts, f => f.Name is "country" or "countryRule");
        Assert.DoesNotContain("countr", step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASimulationLeavesTheCountersAlone()
    {
        var (snapshot, _) = Build(AccessListSatisfy.All, out var runtime);

        _ = GateSimulator.Run(snapshot, Probe("cloudflare", "RU"));
        _ = GateSimulator.Run(snapshot, Probe("cloudflare", "DE"));

        Assert.Equal((0, 0), (runtime.Stats.AddressRefused, runtime.Stats.Allowed));
    }

    private static RuntimeSnapshot TestSnapshotsFor(AccessListSpec list)
    {
        GateFor(AccessListFixtures.NewRuntime(), Model(list, cloudflare: true), out var snapshot);
        return snapshot;
    }

    private static (RuntimeSnapshot Snapshot, GateMiddleware Gate) Build(AccessListSatisfy satisfy) => Build(satisfy, out _);

    private static (RuntimeSnapshot Snapshot, GateMiddleware Gate) Build(AccessListSatisfy satisfy, out AccessListRuntime runtime)
    {
        runtime = AccessListFixtures.NewRuntime();
        var list = List("office", users: false, satisfy: satisfy, allowCountries: AllowedCountries, denyCountries: DeniedCountries);
        var gate = GateFor(runtime, Model(list, cloudflare: true), out var snapshot);
        return (snapshot, gate);
    }

    // A direct client sends the header itself: the peer is nobody's proxy, so the gate must not believe it.
    private static Microsoft.AspNetCore.Http.DefaultHttpContext Forged(string? country, string how)
    {
        var context = Context(Client);
        if (how == "forged" && country is not null)
        {
            context.Request.Headers["CF-IPCountry"] = country;
        }

        return context;
    }

    private static GateProbe Probe(string how, string? country) => how == "cloudflare"
        ? new()
        {
            Host = "acl.test",
            Path = "/",
            ClientAddress = IPAddress.Parse(Client),
            Arrival = ProbeArrival.CloudflareProxy,
            Country = country,
        }
        : new()
        {
            Host = "acl.test",
            Path = "/",
            ClientAddress = IPAddress.Parse(Client),
            Headers = how == "forged" && country is not null ? [new ProbeHeader("CF-IPCountry", country)] : [],
        };

    private static GateStep AccessListStep(GateSimulation run) =>
        Assert.Single(run.Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "accessList" }));

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;
}
