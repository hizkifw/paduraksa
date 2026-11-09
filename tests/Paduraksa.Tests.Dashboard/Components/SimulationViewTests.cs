using Bunit;

using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class SimulationViewTests
{
    [Fact]
    public async Task AForwardedRequest_ShowsTheOutcome_TheDestinations_AndEveryStep()
    {
        UpstreamId upstream = UpstreamId.New();
        SimulationResult result = new(
            SimulationStatus.Simulated,
            new SimulationSource(SimulationTargetKind.Live),
            new SimulationOutcome(SimulationOutcomeKind.ForwardToUpstream, "forwarded to the route's upstream"),
            [
                new SimulationStep(SimulationStepKind.ClientAddress, SimulationStepVerdict.Passed, "client 192.168.1.20", [new SimulationFact("peer", "192.168.1.20")]),
                new SimulationStep(SimulationStepKind.AcmeChallenge, SimulationStepVerdict.Skipped, "not an ACME path", []),
                new SimulationStep(SimulationStepKind.Upstream, SimulationStepVerdict.Stopped, "forwarded", [new SimulationFact("cluster", upstream.ToString())]),
            ],
            new SimulationUpstream(upstream, [new SimulationDestination(DestinationId.New(), "http://app:8080", SimulationHealth.Healthy)]));
        await using DashboardTestContext context = new();

        IRenderedComponent<SimulationView> view = context.Render<SimulationView>(parameters => parameters
            .Add(v => v.Result, result)
            .Add(v => v.UpstreamName, "app"));

        Assert.Equal("Forwarded to app", view.Find(".pdk-outcome-title").TextContent);
        Assert.Contains("pdk-tone-success", view.Find(".pdk-outcome").ClassName, StringComparison.Ordinal);
        Assert.Contains("http://app:8080", view.Find(".pdk-sim-upstream").TextContent, StringComparison.Ordinal);
        Assert.Equal(
            ["pdk-trace-passed", "pdk-trace-skipped", "pdk-trace-stopped"],
            view.FindAll(".pdk-trace-step").Select(step => step.ClassList.Single(c => c.StartsWith("pdk-trace-", StringComparison.Ordinal) && c != "pdk-trace-step")));
        Assert.Equal(["Client address", "ACME challenge", "Upstream"], view.FindAll(".pdk-trace-title").Select(t => t.TextContent));
        Assert.Equal(["Passed", "Skipped", "Decided"], view.FindAll(".pdk-trace-head .pdk-badge").Select(b => b.TextContent));
        Assert.Equal("Client 192.168.1.20.", view.FindAll(".pdk-trace-summary")[0].TextContent);
        Assert.Contains(view.FindAll(".pdk-fact"), fact => fact.TextContent == "peer192.168.1.20");
    }

    [Fact]
    public async Task ADenial_TakesTheWarningTone_AndShowsItsStatus()
    {
        SimulationResult result = new(
            SimulationStatus.Simulated,
            new SimulationSource(SimulationTargetKind.Live),
            new SimulationOutcome(SimulationOutcomeKind.FixedAnswer, "the IP allow list denies 203.0.113.9", 403),
            [new SimulationStep(SimulationStepKind.HostPolicy, SimulationStepVerdict.Stopped, "denied", [], PolicyIndex: 0)]);
        await using DashboardTestContext context = new();

        IRenderedComponent<SimulationView> view = context.Render<SimulationView>(parameters => parameters.Add(v => v.Result, result));

        Assert.Equal("Answered 403 by the gate", view.Find(".pdk-outcome-title").TextContent);
        Assert.Equal("403", view.Find(".pdk-outcome-status").TextContent);
        Assert.Contains("pdk-tone-warning", view.Find(".pdk-trace-step").ClassName, StringComparison.Ordinal);
        Assert.Equal("Host policy 1", view.Find(".pdk-trace-title").TextContent);
    }

    [Fact]
    public async Task ARequestTheSimulatorRefused_ListsWhy()
    {
        SimulationResult result = new(
            SimulationStatus.BadRequest,
            new SimulationSource(SimulationTargetKind.Live),
            Errors: [new ConfigError("/url", "simulation.request.url", "the URL must be absolute http or https")]);
        await using DashboardTestContext context = new();

        IRenderedComponent<SimulationView> view = context.Render<SimulationView>(parameters => parameters.Add(v => v.Result, result));

        Assert.Empty(view.FindAll(".pdk-trace"));
        Assert.Contains("The URL must be absolute http or https.", view.Find(".pdk-sim-problem").TextContent, StringComparison.Ordinal);
    }
}
