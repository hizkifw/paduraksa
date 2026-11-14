using System.Text;
using System.Text.Json;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

/// <summary>The parent's side of <c>paduraksa.request</c>: what the child sends is validated by name and judged by the policy before anything is dispatched.</summary>
public sealed class RequestBridgeTests
{
    private static readonly RequestPolicy Policy = RequestPolicy.FromSpec(JsonDocument.Parse(Encoding.UTF8.GetBytes(McpTestSpec.OpenApi)).RootElement.Clone());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> AskAsync(string options, ScriptedDispatcher dispatcher, SandboxLimits? limits = null)
    {
        RequestBridge bridge = new(Policy, dispatcher, limits ?? SandboxLimits.Default);
        var frame = await bridge.HandleAsync(7, options, Ct);
        using var message = Messages.Parse(frame);
        Assert.Equal("response", Messages.TypeOf(message));
        Assert.Equal(7, Messages.Number(message.RootElement, "id"));
        return message.RootElement.Clone();
    }

    [Fact]
    public async Task AWriteWithoutConfirmIsADryRunWhereTheOperationHasOne()
    {
        ScriptedDispatcher dispatcher = new(_ => new ApiReply(200, "application/json", "{\"plan\":[1]}"));
        var reply = await AskAsync("""{"method":"put","path":"/api/v1/hosts/host_abc","body":{"name":"a"},"ifMatch":"\"3\""}""", dispatcher);
        Assert.Equal(200, reply.GetProperty("status").GetInt32());
        Assert.True(reply.GetProperty("dryRun").GetBoolean());
        Assert.Equal("{\"plan\":[1]}", reply.GetProperty("body").GetString());
        Assert.True(reply.GetProperty("json").GetBoolean());
        Assert.Contains("Dry run", reply.GetProperty("note").GetString(), StringComparison.Ordinal);
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal("PUT", call.Method);
        Assert.Contains(new KeyValuePair<string, string>("dryRun", "true"), call.Query);
        Assert.Equal("{\"name\":\"a\"}", call.BodyJson);
        Assert.Equal("\"3\"", call.IfMatch);
    }

    [Fact]
    public async Task AConfirmedWriteIsSentAsItIsWithNoDryRun()
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync("""{"method":"POST","path":"/api/v1/hosts","body":{"name":"a"},"confirm":true}""", dispatcher);
        Assert.False(reply.TryGetProperty("dryRun", out _));
        Assert.Empty(Assert.Single(dispatcher.Calls).Query);
    }

    [Fact]
    public async Task AWriteWithNoDryRunAndNoConfirmIsNotSentAnd428IsTheAnswer()
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync("""{"method":"DELETE","path":"/api/v1/hosts/host_abc"}""", dispatcher);
        Assert.Empty(dispatcher.Calls);
        Assert.Equal(428, reply.GetProperty("status").GetInt32());
        using var body = JsonDocument.Parse(reply.GetProperty("body").GetString()!);
        Assert.True(body.RootElement.GetProperty("requiresConfirm").GetBoolean());
        Assert.Equal("DELETE", body.RootElement.GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("\"confirm\":\"yes\"")]
    [InlineData("\"confirm\":1")]
    [InlineData("\"confirm\":null")]
    public async Task OnlyTheBooleanTrueConfirms(string confirm)
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync($$"""{"method":"DELETE","path":"/api/v1/hosts/host_abc",{{confirm}}}""", dispatcher);
        Assert.Empty(dispatcher.Calls);
        Assert.True(reply.TryGetProperty("error", out _) || reply.GetProperty("status").GetInt32() == 428);
    }

    [Fact]
    public async Task AReadOnlyPostNeedsNoConfirm()
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync("""{"method":"POST","path":"/api/v1/simulate","body":{"host":"a"}}""", dispatcher);
        Assert.Equal(200, reply.GetProperty("status").GetInt32());
        Assert.Single(dispatcher.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tokens", "API tokens")]
    [InlineData("POST", "/api/v1/dns-providers", "DNS credential")]
    [InlineData("GET", "/api/v1/logs/tail", "logs/recent")]
    [InlineData("GET", "/api/v1/nothing", "search")]
    [InlineData("GET", "/api/v1/hosts/../tokens", "plain path")]
    [InlineData("POST", "/mcp", "plain path")]
    [InlineData("POST", "/MCP", "plain path")]
    [InlineData("POST", "/mcp/", "plain path")]
    public async Task ARefusedRequestIsAnErrorWithAReasonAndNothingIsSent(string method, string path, string reason)
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync($$"""{"method":"{{method}}","path":"{{path}}","confirm":true}""", dispatcher);
        Assert.Empty(dispatcher.Calls);
        Assert.Equal("Error", reply.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains(reason, reply.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"method\":\"GET\"}", "method and path are required")]
    [InlineData("{\"path\":\"/api/v1/hosts\"}", "method and path are required")]
    [InlineData("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"headers\":{\"x\":\"y\"}}", "Unknown option 'headers'")]
    [InlineData("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"body\":{}}", "A GET takes no body")]
    [InlineData("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"query\":{\"dryRun\":\"false\"}}", "dryRun is the client's")]
    [InlineData("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"query\":{\"a\":{\"b\":1}}}", "must be a string, number or boolean")]
    [InlineData("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"query\":[1]}", "query must be an object")]
    [InlineData("{\"method\":\"PUT\",\"path\":\"/api/v1/hosts/host_abc\",\"ifMatch\":\"not quoted\"}", "ifMatch must be the quoted etag")]
    [InlineData("{\"method\":\"PUT\",\"path\":\"/api/v1/hosts/host_abc\",\"ifMatch\":\"\\\"a\\r\\nX: y\\\"\"}", "ifMatch must be the quoted etag")]
    [InlineData("\"GET /api/v1/hosts\"", "request takes one object")]
    [InlineData("null", "request takes one object")]
    [InlineData("not json at all", "request takes one object")]
    public async Task ABadRequestObjectIsATypeErrorNamingWhatIsWrong(string options, string message)
    {
        ScriptedDispatcher dispatcher = new();
        var reply = await AskAsync(options, dispatcher);
        Assert.Empty(dispatcher.Calls);
        Assert.Equal("TypeError", reply.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains(message, reply.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQueryTakesStringsNumbersBooleansAndArraysAndDropsNulls()
    {
        ScriptedDispatcher dispatcher = new();
        await AskAsync("""{"method":"GET","path":"/api/v1/hosts","query":{"limit":5,"tag":["a","b"],"on":true,"none":null,"s":"x y"}}""", dispatcher);
        Assert.Equal([new("limit", "5"), new("tag", "a"), new("tag", "b"), new("on", "true"), new("s", "x y")], Assert.Single(dispatcher.Calls).Query);
    }

    [Fact]
    public async Task ABodyOverTheLimitIsRefusedAndAnAnswerCarriesItsEtagAndTruncationNote()
    {
        ScriptedDispatcher none = new();
        var big = await AskAsync("{\"method\":\"POST\",\"path\":\"/api/v1/hosts\",\"confirm\":true,\"body\":{\"t\":\"" + new string('x', 3000) + "\"}}", none, new SandboxLimits { MaxRequestBodyBytes = 1024 });
        Assert.Contains("larger than 1 KiB", big.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(none.Calls);

        ScriptedDispatcher answers = new(c => c.Path.EndsWith("/hosts", StringComparison.Ordinal)
            ? new ApiReply(200, "application/json", null, "\"9\"", Truncated: true)
            : new ApiReply(200, "text/plain", "plain", "\"3\""));
        var truncated = await AskAsync("""{"method":"GET","path":"/api/v1/hosts"}""", answers);
        Assert.Equal(JsonValueKind.Null, truncated.GetProperty("body").ValueKind);
        Assert.Equal("\"9\"", truncated.GetProperty("etag").GetString());
        Assert.Contains("larger than 1024 KiB", truncated.GetProperty("note").GetString(), StringComparison.Ordinal);
        var text = await AskAsync("""{"method":"GET","path":"/api/v1/ca"}""", answers);
        Assert.Equal("plain", text.GetProperty("body").GetString());
        Assert.False(text.GetProperty("json").GetBoolean());
    }

    [Fact]
    public void AFailedDispatchSaysNothingOfTheFailure()
    {
        using var message = Messages.Parse(RequestBridge.DispatchFailed(4));
        Assert.Equal("The request failed inside the server; nothing more is known.", message.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("[", "]")]
    [InlineData("{\"a\":", "}")]
    public async Task ABodyNestedToTheLimitIsSentAndOneLevelMoreIsRefusedByName(string open, string close)
    {
        static string Nested(string open, string close, int levels) => string.Concat(Enumerable.Repeat(open, levels)) + (open == "[" ? "" : "1") + string.Concat(Enumerable.Repeat(close, levels));

        ScriptedDispatcher dispatcher = new();
        var atLimit = Nested(open, close, SandboxLimits.MaxBodyDepth);
        var sent = await AskAsync($$"""{"method":"POST","path":"/api/v1/simulate","body":{{atLimit}}}""", dispatcher);
        Assert.Equal(200, sent.GetProperty("status").GetInt32());
        Assert.Equal(atLimit, Assert.Single(dispatcher.Calls).BodyJson);

        foreach (var levels in new[] { SandboxLimits.MaxBodyDepth + 1, 10_000 })
        {
            var refused = await AskAsync($$"""{"method":"POST","path":"/api/v1/simulate","confirm":true,"body":{{Nested(open, close, levels)}}}""", dispatcher);
            Assert.Equal("TypeError", refused.GetProperty("error").GetProperty("type").GetString());
            Assert.Equal("The request body is nested deeper than 32 levels.", refused.GetProperty("error").GetProperty("message").GetString());
        }

        Assert.Single(dispatcher.Calls);
    }

    [Fact]
    public async Task DeepOptionsOutsideTheBodyAreStillTheShapeError()
    {
        var reply = await AskAsync("{\"method\":\"GET\",\"path\":\"/api/v1/hosts\",\"query\":" + new string('[', 100) + new string(']', 100) + "}", new ScriptedDispatcher());
        Assert.StartsWith("request takes one object", reply.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
