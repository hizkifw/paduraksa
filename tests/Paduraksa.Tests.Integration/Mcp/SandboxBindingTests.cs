using Paduraksa.Mcp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>
/// The <c>paduraksa</c> global is there on every run of execute, whatever the child ran before, and the forms models write it in all reach it: a red-team
/// agent saw it missing, and the cause was a function written to take its globals as an argument or from <c>this</c>.
/// </summary>
public sealed class SandboxBindingTests
{
    private const string Probe =
        "async () => [typeof paduraksa, typeof globalThis.paduraksa, typeof paduraksa.request, (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status]";

    private const string Present = "[\"object\",\"object\",\"function\",200]";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("async () => typeof paduraksa.request")]
    [InlineData("async ({ paduraksa }) => typeof paduraksa.request")]
    [InlineData("async ({ paduraksa: { request } }) => typeof request")]
    [InlineData("(async function () { return typeof this.paduraksa.request; })")]
    [InlineData("async () => typeof new Function('return paduraksa')().request")]
    public async Task EveryWayOfReachingTheClientReachesIt(string code)
    {
        await using var tools = SandboxFixture.Create();
        var outcome = await tools.ExecuteAsync(code, new ScriptedDispatcher(), Ct);
        Assert.Equal("\"function\"", outcome.Text);
    }

    [Theory]
    [InlineData("async ({ spec }) => typeof spec.paths", "\"object\"")]
    [InlineData("async (globals) => Object.keys(globals).join()", "\"spec\"")]
    public async Task SearchPassesItsSpecTheSameWay(string code, string expected)
    {
        await using var tools = SandboxFixture.Create();
        Assert.Equal(expected, (await tools.SearchAsync(code, Ct)).Text);
    }

    [Fact]
    public async Task TheClientIsThereOnEveryRunOfOneChildAndOfConcurrentOnes()
    {
        await using var tools = SandboxFixture.Create();
        ScriptedDispatcher dispatcher = new();
        for (var i = 0; i < 60; i++)
        {
            var outcome = await tools.ExecuteAsync(Probe, dispatcher, Ct);
            Assert.True(outcome.Text == Present, $"run {i}: {outcome.Text}");
        }

        for (var round = 0; round < 10; round++)
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => tools.ExecuteAsync(Probe, dispatcher, Ct)));
            Assert.All(outcomes, outcome => Assert.Equal(Present, outcome.Text));
        }
    }

    [Theory]
    [InlineData("async () => { delete globalThis.paduraksa; return 1; }")]
    [InlineData("async () => { globalThis.paduraksa = undefined; return 1; }")]
    [InlineData("async () => { paduraksa.request = null; return 1; }")]
    [InlineData("async () => { Object.defineProperty(globalThis, 'paduraksa', { value: 1 }); return 1; }")]
    [InlineData("async () => { Object.freeze(globalThis); Object.freeze(Reflect); return 1; }")]
    [InlineData("async () => { Promise.prototype.then = null; Reflect.apply = null; return 1; }")]
    [InlineData("async () => { while (true) {} }")]
    public async Task WhatARunDoesToTheGlobalsDoesNotReachTheNextRunOnTheSameChild(string hostile)
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { MaxChildren = 1 });
        ScriptedDispatcher dispatcher = new();
        await tools.ExecuteAsync(hostile, dispatcher, Ct);
        await tools.SearchAsync("async () => { globalThis.paduraksa = 1; return 1; }", Ct);
        Assert.Equal(Present, (await tools.ExecuteAsync(Probe, dispatcher, Ct)).Text);
    }

    [Fact]
    public async Task AChildStartedAfterTheIdleOneWasDespawnedHasTheClient()
    {
        ManualTimeProvider time = new();
        await using var tools = SandboxFixture.Create(idle: TimeSpan.FromMinutes(1), time: time);
        ScriptedDispatcher dispatcher = new();
        Assert.Equal(Present, (await tools.ExecuteAsync(Probe, dispatcher, Ct)).Text);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, tools.Pool.ChildCount);
        Assert.Equal(Present, (await tools.ExecuteAsync(Probe, dispatcher, Ct)).Text);
    }
}
