using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

public sealed class ExecuteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<SandboxOutcome> RunAsync(string body, ScriptedDispatcher dispatcher, Action<Exception>? unexpected = null, SandboxLimits? limits = null) =>
        McpTestSpec.Tools(limits, unexpected).ExecuteAsync($"async () => {{ {body} }}", dispatcher, Ct);

    [Fact]
    public async Task AWriteWithoutConfirmIsADryRunWhereTheOperationHasOne()
    {
        ScriptedDispatcher dispatcher = new(_ => new ApiReply(200, "application/json", "{\"plan\":[1]}"));
        var outcome = await RunAsync("return await paduraksa.request({ method: 'PUT', path: '/api/v1/hosts/host_abc', body: { name: 'a' }, ifMatch: '\"3\"' });", dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("{\"status\":200,\"body\":{\"plan\":[1]},\"dryRun\":true,\"note\":\"Dry run: nothing was changed. Repeat the request with confirm: true to apply it.\"}", outcome.Text);
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
        var outcome = await RunAsync("return await paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: { name: 'a' }, confirm: true });", dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.DoesNotContain("dryRun", outcome.Text, StringComparison.Ordinal);
        var call = Assert.Single(dispatcher.Calls);
        Assert.Empty(call.Query);
    }

    [Fact]
    public async Task AWriteWithNoDryRunAndNoConfirmIsNotSentAndAnswers428()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync("return await paduraksa.request({ method: 'DELETE', path: '/api/v1/hosts/host_abc' });", dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Empty(dispatcher.Calls);
        Assert.StartsWith("{\"status\":428,\"body\":{\"requiresConfirm\":true,\"method\":\"DELETE\",\"path\":\"/api/v1/hosts/host_abc\",\"note\":", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("confirm: 'yes'")]
    [InlineData("confirm: 1")]
    [InlineData("confirm: null")]
    public async Task OnlyTheBooleanTrueConfirms(string confirm)
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync($"return await paduraksa.request({{ method: 'DELETE', path: '/api/v1/hosts/host_abc', {confirm} }});", dispatcher);
        Assert.Empty(dispatcher.Calls);
        Assert.True(outcome.IsError || outcome.Text.Contains("428", StringComparison.Ordinal), outcome.Text);
    }

    [Fact]
    public async Task AReadOnlyPostNeedsNoConfirm()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync("return (await paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: { host: 'a' } })).status;", dispatcher);
        Assert.Equal("200", outcome.Text);
        Assert.Single(dispatcher.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tokens", "API tokens")]
    [InlineData("POST", "/api/v1/dns-providers", "DNS credential")]
    [InlineData("GET", "/api/v1/logs/tail", "logs/recent")]
    [InlineData("GET", "/api/v1/nothing", "search")]
    [InlineData("GET", "/api/v1/hosts/../tokens", "plain path")]
    public async Task ARefusedRequestRejectsWithAReasonAndSendsNothing(string method, string path, string reason)
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync($"return await paduraksa.request({{ method: '{method}', path: '{path}', confirm: true }});", dispatcher);
        Assert.True(outcome.IsError);
        Assert.Contains(reason, outcome.Text, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Calls);

        var caught = await RunAsync($"try {{ await paduraksa.request({{ method: '{method}', path: '{path}', confirm: true }}); return 'sent'; }} catch (e) {{ return e.message; }}", dispatcher);
        Assert.Contains(reason, caught.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/MCP")]
    [InlineData("/mcp/")]
    [InlineData("/api/v1/../../mcp")]
    public async Task ARequestToTheMcpEndpointItselfIsRefusedAndNothingIsSent(string path)
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync($"return await paduraksa.request({{ method: 'POST', path: '{path}', body: {{ jsonrpc: '2.0', id: 1, method: 'tools/list' }}, confirm: true }});", dispatcher);
        Assert.True(outcome.IsError);
        Assert.Contains("plain path", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Calls);
    }

    [Theory]
    [InlineData("{ method: 'GET' }", "method and path are required")]
    [InlineData("{ path: '/api/v1/hosts' }", "method and path are required")]
    [InlineData("{ method: 'GET', path: '/api/v1/hosts', headers: { x: 'y' } }", "Unknown option 'headers'")]
    [InlineData("{ method: 'GET', path: '/api/v1/hosts', body: {} }", "A GET takes no body")]
    [InlineData("{ method: 'GET', path: '/api/v1/hosts', query: { dryRun: 'false' } }", "dryRun is the client's")]
    [InlineData("{ method: 'GET', path: '/api/v1/hosts', query: { a: { b: 1 } } }", "must be a string, number or boolean")]
    [InlineData("{ method: 'GET', path: '/api/v1/hosts', query: [1] }", "query must be an object")]
    [InlineData("{ method: 'PUT', path: '/api/v1/hosts/host_abc', ifMatch: 'not quoted' }", "ifMatch must be the quoted etag")]
    [InlineData("{ method: 'PUT', path: '/api/v1/hosts/host_abc', ifMatch: '\"a\\r\\nX: y\"' }", "ifMatch must be the quoted etag")]
    [InlineData("'GET /api/v1/hosts'", "request takes one object")]
    [InlineData("undefined", "request takes one object")]
    public async Task ABadRequestObjectIsATypeErrorNamingWhatIsWrong(string argument, string message)
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync($"return await paduraksa.request({argument});", dispatcher);
        Assert.True(outcome.IsError);
        Assert.StartsWith("TypeError: ", outcome.Text, StringComparison.Ordinal);
        Assert.Contains(message, outcome.Text, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task ABodyOverTheLimitIsRefused()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync("return await paduraksa.request({ method: 'POST', path: '/api/v1/hosts', confirm: true, body: { text: 'x'.repeat(3000) } });", dispatcher, limits: new SandboxLimits { MaxRequestBodyBytes = 1024 });
        Assert.True(outcome.IsError);
        Assert.Contains("larger than 1 KiB", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFiftyFirstRequestOfACallIsRefusedAndRefusedOnesCount()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync(
            "let n = 0; try { for (; n < 60; n++) { await paduraksa.request({ method: 'GET', path: n % 2 ? '/api/v1/tokens' : '/api/v1/hosts' }).catch(() => { }); } } catch (e) { return { n, message: e.message }; } return { n };",
            dispatcher,
            limits: new SandboxLimits { MaxRequests = 50 });
        Assert.Equal(50, outcome.Requests > 50 ? 50 : outcome.Requests);
        Assert.Equal(25, dispatcher.Calls.Count);

        var thrown = await RunAsync("for (let n = 0; n < 60; n++) { await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); } return 1;", dispatcher, limits: new SandboxLimits { MaxRequests = 50 });
        Assert.True(thrown.IsError);
        Assert.StartsWith("RangeError: At most 50 requests per call", thrown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsRunOneAtATimeInTheOrderTheyWereMadeAndUnawaitedOnesStillRun()
    {
        List<string> order = [];
        ScriptedDispatcher dispatcher = new(call =>
        {
            order.Add(call.Path);
            return new ApiReply(200, "application/json", "{}");
        });
        var outcome = await RunAsync(
            "const a = paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_a' }); const b = paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_b' }); await b; paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: {}, confirm: true }); return 'done';",
            dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("\"done\"", outcome.Text);
        Assert.Equal(["/api/v1/hosts/host_a", "/api/v1/hosts/host_b", "/api/v1/hosts"], order);
        Assert.Contains("1 request(s) were still pending", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAnswerIsParsedJsonOrTextOrNull()
    {
        ScriptedDispatcher dispatcher = new(call => call.Path switch
        {
            "/api/v1/hosts" => new ApiReply(200, "application/problem+json; charset=utf-8", "{\"title\":\"x\"}"),
            "/api/v1/ca" => new ApiReply(200, "text/plain", "plain text"),
            "/api/v1/hosts/host_a" => new ApiReply(204, null, null),
            "/api/v1/hosts/host_b" => new ApiReply(500, "application/json", "{not json"),
            _ => new ApiReply(200, "application/json", null, Truncated: true),
        });
        var outcome = await RunAsync(
            "const get = (p) => paduraksa.request({ method: 'GET', path: p }); return [await get('/api/v1/hosts'), await get('/api/v1/ca'), await get('/api/v1/hosts/host_a'), await get('/api/v1/hosts/host_b'), await get('/api/v1/logs/recent')];",
            dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.StartsWith("[{\"status\":200,\"body\":{\"title\":\"x\"}},{\"status\":200,\"body\":\"plain text\"},{\"status\":204,\"body\":null},{\"status\":500,\"body\":\"{not json\"},{\"status\":200,\"body\":null,\"note\":\"The answer is larger than 1024 KiB", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADispatcherThatFailsIsARejectionThatSaysNothingOfTheFailure()
    {
        List<Exception> logged = [];
        ScriptedDispatcher dispatcher = new(_ => throw new InvalidOperationException("secret detail at Paduraksa.Admin.Mcp.Dispatcher line 42"));
        var outcome = await RunAsync("return await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' });", dispatcher, logged.Add);
        Assert.True(outcome.IsError);
        Assert.StartsWith("Error: The request failed inside the server; nothing more is known.", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", outcome.Text, StringComparison.Ordinal);
        Assert.Single(logged);
    }

    [Fact]
    public async Task AnAnswerWithPrototypeShapedKeysIsDataAndPollutesNothing()
    {
        ScriptedDispatcher dispatcher = new(_ => new ApiReply(200, "application/json", "{\"__proto__\":{\"polluted\":true},\"constructor\":{\"prototype\":{\"polluted\":true}},\"toString\":\"x\",\"a\":[{\"__proto__\":[1]}]}"));
        var outcome = await RunAsync(
            "const r = await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); return { polluted: typeof ({}).polluted, own: Object.keys(r.body), samePrototype: Object.getPrototypeOf(r.body) === Object.prototype, typeOfConstructor: typeof r.body.constructor };",
            dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("{\"polluted\":\"undefined\",\"own\":[\"__proto__\",\"constructor\",\"toString\",\"a\"],\"samePrototype\":true,\"typeOfConstructor\":\"object\"}", outcome.Text);
    }

    [Fact]
    public async Task TheScriptSeesOnlyTheRequestFunctionAndNothingOfTheDispatcher()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await RunAsync(
            "return { keys: Object.keys(paduraksa), type: typeof paduraksa.request, source: String(paduraksa.request), proto: Object.getOwnPropertyNames(Object.getPrototypeOf(paduraksa.request)).includes('call'), props: Object.getOwnPropertyNames(paduraksa.request).sort() };",
            dispatcher);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Contains("\"keys\":[\"request\"]", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("[native code]", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", outcome.Text, StringComparison.Ordinal);
    }
}
