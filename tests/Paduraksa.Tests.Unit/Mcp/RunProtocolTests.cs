using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

/// <summary>The parent's run protocol binds every frame of the child to the run in progress: the child is not trusted to be a child.</summary>
public sealed class RunProtocolTests
{
    private const string RunId = "RUN-A";

    private static readonly RequestPolicy Policy = RequestPolicy.FromSpec(JsonDocument.Parse(McpTestSpec.OpenApi).RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Frames(params string[] json)
    {
        List<byte> bytes = [];
        foreach (var frame in json)
        {
            var body = Encoding.UTF8.GetBytes(frame);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
            bytes.AddRange(prefix);
            bytes.AddRange(body);
        }

        return [.. bytes];
    }

    private static Task<RunProtocol.Exchange> ExchangeAsync(byte[] fromChild, ScriptedDispatcher dispatcher, string runId = RunId) =>
        RunProtocol.ExchangeAsync(new MemoryStream(fromChild), new MemoryStream(), new RequestBridge(Policy, dispatcher, SandboxLimits.Default), SandboxLimits.Default, new RunState(runId), _ => { }, Ct);

    [Fact]
    public async Task AResultAndARequestThatCarryTheRunIdAreAccepted()
    {
        ScriptedDispatcher dispatcher = new();
        var exchange = await ExchangeAsync(
            Frames(
                """{"t":"request","rid":"RUN-A","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}""",
                """{"t":"result","rid":"RUN-A","ok":true,"value":"7","truncated":false}"""),
            dispatcher);
        Assert.Equal("7", exchange.Outcome.Text);
        Assert.Single(dispatcher.Calls);
    }

    [Theory]
    [InlineData("""{"t":"result","ok":true,"value":"\"FABRICATED\"","truncated":false}""")]
    [InlineData("""{"t":"result","rid":"RUN-B","ok":true,"value":"\"FABRICATED\"","truncated":false}""")]
    [InlineData("""{"t":"result","rid":"","ok":true,"value":"\"FABRICATED\"","truncated":false}""")]
    [InlineData("""{"t":"result","rid":"run-a","ok":true,"value":"\"FABRICATED\"","truncated":false}""")]
    [InlineData("""{"t":"result","rid":7,"ok":true,"value":"\"FABRICATED\"","truncated":false}""")]
    public async Task AResultForAnotherRunOrNoRunIsAViolation(string frame) =>
        await Assert.ThrowsAsync<SandboxProtocolException>(() => ExchangeAsync(Frames(frame), new ScriptedDispatcher()));

    [Theory]
    [InlineData("""{"t":"request","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}""")]
    [InlineData("""{"t":"request","rid":"RUN-OLD","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}""")]
    public async Task ARequestForAnotherRunIsNeverDispatched(string frame)
    {
        ScriptedDispatcher dispatcher = new();
        await Assert.ThrowsAsync<SandboxProtocolException>(() => ExchangeAsync(Frames(frame), dispatcher));
        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task EachRunHasItsOwnIdSoAFrameOfTheLastOneFailsTheNext()
    {
        var frames = Frames("""{"t":"result","rid":"RUN-A","ok":true,"value":"1","truncated":false}""");
        Assert.Equal("1", (await ExchangeAsync(frames, new ScriptedDispatcher(), "RUN-A")).Outcome.Text);
        await Assert.ThrowsAsync<SandboxProtocolException>(() => ExchangeAsync(frames, new ScriptedDispatcher(), "RUN-B"));
    }

    [Fact]
    public void TheRunFrameCarriesTheRunId()
    {
        using var message = Messages.Parse(RunProtocol.RunFrame("search", "async () => 1", SandboxLimits.Default, "abc123"));
        Assert.Equal("abc123", message.RootElement.GetProperty("rid").GetString());
    }
}
