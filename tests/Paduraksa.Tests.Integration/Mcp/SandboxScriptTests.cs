using Paduraksa.Mcp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>What a script gets from a real child process (docs/design/admin-surfaces.md "MCP in code mode (M10)"): values, errors with their positions, the spec, and the request round trip through the parent.</summary>
public sealed class SandboxScriptTests(SandboxFixture fixture) : IClassFixture<SandboxFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CodeModeTools Tools => fixture.Tools;

    [Theory]
    [InlineData("async () => 1 + 1", "2")]
    [InlineData("async () => ({ a: [1, 'x', null], b: { c: true } })", "{\"a\":[1,\"x\",null],\"b\":{\"c\":true}}")]
    [InlineData("() => undefined", "null")]
    [InlineData("const k = 3; async () => k * 2", "6")]
    [InlineData("async () => { const r = await Promise.all([1, Promise.resolve(2)]); return r; }", "[1,2]")]
    [InlineData("async () => new Date(0)", "\"1970-01-01T00:00:00.000Z\"")]
    [InlineData("async () => ({ f() {}, u: undefined, s: Symbol('x'), n: NaN, i: Infinity })", "{\"n\":null,\"i\":null}")]
    [InlineData("async () => [3, 1, 2].sort().join('-') + String([1, [2, 3]])", "\"1-2-31,2,3\"")]
    [InlineData("async () => 'é€😀'", "\"é€😀\"")]
    public async Task AScriptReturnsItsValueAsJson(string code, string json)
    {
        var outcome = await Tools.SearchAsync(code, Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal(json, outcome.Text);
    }

    [Theory]
    [InlineData("async () => { let x = ; }", "SyntaxError: Unexpected token ';' (line 1, column 23)")]
    [InlineData("async () => {\n  const a = 1;\n  return a +* 2;\n}", "SyntaxError: Unexpected token '*' (line 3, column 13)")]
    [InlineData("async () => {\n  null.x;\n}", "TypeError: Cannot read properties of null (reading 'x') (line 2, column 8)")]
    [InlineData("async () => { throw new RangeError('too far'); }", "RangeError: too far (line 1, column 21)")]
    [InlineData("async () => { throw { code: 7 }; }", "Uncaught {\"code\":7}")]
    [InlineData("async () => { throw 'plain'; }", "Uncaught \"plain\"")]
    [InlineData("async () => undefinedFunction()", "ReferenceError: undefinedFunction is not defined (line 1, column 13)")]
    [InlineData("return 1", "The code must evaluate to a function")]
    [InlineData("async function named() {}", "The code must evaluate to a function")]
    [InlineData("(1, 2)", "The code must evaluate to a function")]
    [InlineData("async () => { await new Promise(() => {}); }", "The function never finished")]
    [InlineData("async () => { const o = {}; o.self = o; return o; }", "TypeError")]
    [InlineData("   ", "The code is empty")]
    public async Task AFailureNamesTheErrorAndWhereItWas(string code, string expected)
    {
        var outcome = await Tools.SearchAsync(code, Ct);
        Assert.True(outcome.IsError);
        Assert.StartsWith(expected, outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Paduraksa", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeLongerThanTheLimitIsRefusedBeforeAChildSeesIt()
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { MaxCodeChars = 50 });
        var outcome = await tools.SearchAsync("async () => '" + new string('x', 100) + "'", Ct);
        Assert.True(outcome.IsError);
        Assert.Contains("the limit is 50", outcome.Text, StringComparison.Ordinal);
        Assert.Equal(0, tools.Pool.ChildCount);
    }

    [Theory]
    [InlineData("typeof require")]
    [InlineData("typeof process")]
    [InlineData("typeof fetch")]
    [InlineData("typeof setTimeout")]
    [InlineData("typeof console")]
    [InlineData("typeof Buffer")]
    [InlineData("typeof module")]
    [InlineData("typeof importNamespace")]
    [InlineData("typeof clr")]
    [InlineData("typeof System")]
    [InlineData("typeof paduraksa")]
    public async Task NothingOfTheHostIsInTheGlobalScope(string probe)
    {
        var outcome = await Tools.SearchAsync($"async () => {probe}", Ct);
        Assert.Equal("\"undefined\"", outcome.Text);
    }

    [Fact]
    public async Task SearchReadsTheSpecWithItsReferencesResolvedAndSharedWhereTheyAreTheSame()
    {
        var outcome = await Tools.SearchAsync(
            """
            async () => {
              const post = spec.paths['/api/v1/hosts'].post.requestBody.content['application/json'].schema;
              const list = spec.paths['/api/v1/hosts'].get.responses['200'].content['application/json'].schema.items;
              return {
                settings: post.properties.settings,
                sameObject: post === list,
                parameter: spec.paths['/api/v1/hosts/{id}'].get.parameters[0].name,
                recursive: post.properties.tree.properties.children.items,
                named: Object.keys(spec.components.schemas).sort(),
                bearer: spec.components.securitySchemes.bearer.scheme,
                refLeft: JSON.stringify(spec).includes('$ref'),
              };
            }
            """,
            Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Contains("\"settings\":{\"type\":\"object\",\"properties\":{\"hsts\":{\"type\":\"boolean\"}}}", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"sameObject\":true", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"parameter\":\"id\"", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"recursive\":{\"x-recursive-ref\":\"Node\"}", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"named\":[\"Host\",\"Node\",\"Settings\"]", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"bearer\":\"bearer\"", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"refLeft\":false", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachRunHasAFreshEngineEvenOnTheSameChild()
    {
        var first = await Tools.SearchAsync("async () => { globalThis.leak = 1; Array.prototype.poison = 2; Object.prototype.polluted = true; spec.paths = null; return 1; }", Ct);
        Assert.False(first.IsError, first.Text);
        var second = await Tools.SearchAsync("async () => [typeof leak, typeof [].poison, typeof spec.paths, typeof ({}).polluted]", Ct);
        Assert.Equal("[\"undefined\",\"undefined\",\"object\",\"undefined\"]", second.Text);
    }

    [Fact]
    public async Task AResultOverTheCapIsCutAndSaysSo()
    {
        var outcome = await Tools.SearchAsync("async () => Array.from({ length: 5000 }, (_, i) => ({ index: i, text: 'row ' + i }))", Ct);
        Assert.False(outcome.IsError);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(outcome.Text) <= 64 * 1024);
        Assert.Contains("64 KiB", outcome.Note, StringComparison.Ordinal);
        Assert.StartsWith("[{\"index\":0,\"text\":\"row 0\"}", outcome.Text, StringComparison.Ordinal);

        await using var small = SandboxFixture.Create(new SandboxLimits { MaxOutputBytes = 1000 });
        var cut = await small.SearchAsync("async () => 'é'.repeat(2000)", Ct);
        Assert.NotNull(cut.Note);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut.Text) <= 1000);
    }

    [Fact]
    public async Task ExecuteAwaitsTheRequestsOfAFunctionOneAtATimeInTheOrderTheyWereMade()
    {
        List<string> order = [];
        ScriptedDispatcher dispatcher = new(call =>
        {
            order.Add(call.Path);
            return new ApiReply(200, "application/json", "{\"ok\":true}", "\"3\"");
        });
        var outcome = await Tools.ExecuteAsync(
            "async () => { const a = await paduraksa.request({ method: 'GET', path: '/api/v1/hosts', query: { limit: 5, tag: ['a', 'b'] } }); const [b, c] = await Promise.all([paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_x' }), paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_y' })]); return [a, b.status, c.etag]; }",
            dispatcher,
            Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("[{\"status\":200,\"body\":{\"ok\":true},\"etag\":\"\\\"3\\\"\"},200,\"\\\"3\\\"\"]", outcome.Text);
        Assert.Equal(3, outcome.Requests);
        Assert.Equal(["/api/v1/hosts", "/api/v1/hosts/host_x", "/api/v1/hosts/host_y"], order);
        Assert.Equal([new("limit", "5"), new("tag", "a"), new("tag", "b")], dispatcher.Calls[0].Query);
    }

    [Fact]
    public async Task ARefusedRequestRejectsInTheScriptAndANeverSentWriteIsA428()
    {
        ScriptedDispatcher dispatcher = new();
        var refused = await Tools.ExecuteAsync("async () => { try { await paduraksa.request({ method: 'GET', path: '/api/v1/tokens' }); return 'sent'; } catch (e) { return e.name + ': ' + e.message; } }", dispatcher, Ct);
        Assert.StartsWith("\"Error: Not available through this tool: a script that could mint or revoke API tokens", refused.Text, StringComparison.Ordinal);

        var write = await Tools.ExecuteAsync("async () => paduraksa.request({ method: 'DELETE', path: '/api/v1/hosts/host_abc' })", dispatcher, Ct);
        Assert.StartsWith("{\"status\":428,\"body\":{\"requiresConfirm\":true,\"method\":\"DELETE\"", write.Text, StringComparison.Ordinal);

        var bad = await Tools.ExecuteAsync("async () => paduraksa.request({ method: 'GET', path: '/api/v1/hosts', headers: {} })", dispatcher, Ct);
        Assert.StartsWith("TypeError: Unknown option 'headers'", bad.Text, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task ADryRunComesBackFlaggedAndAConfirmedWriteIsSentPlain()
    {
        ScriptedDispatcher dispatcher = new(_ => new ApiReply(200, "application/json", "{\"plan\":[1]}"));
        var dry = await Tools.ExecuteAsync("async () => paduraksa.request({ method: 'PUT', path: '/api/v1/hosts/host_a', body: { a: 1 }, ifMatch: '\"3\"' })", dispatcher, Ct);
        Assert.Equal("{\"status\":200,\"body\":{\"plan\":[1]},\"dryRun\":true,\"note\":\"Dry run: nothing was changed. Repeat the request with confirm: true to apply it.\"}", dry.Text);
        var made = await Tools.ExecuteAsync("async () => paduraksa.request({ method: 'PUT', path: '/api/v1/hosts/host_a', body: { a: 1 }, ifMatch: '\"3\"', confirm: true })", dispatcher, Ct);
        Assert.DoesNotContain("dryRun", made.Text, StringComparison.Ordinal);
        Assert.Contains(new KeyValuePair<string, string>("dryRun", "true"), dispatcher.Calls[0].Query);
        Assert.Empty(dispatcher.Calls[1].Query);
        Assert.Equal("\"3\"", dispatcher.Calls[1].IfMatch);
    }

    [Fact]
    public async Task RequestsTheFunctionLeftUnawaitedStillRunAndTheResultSaysSo()
    {
        List<string> order = [];
        ScriptedDispatcher dispatcher = new(call =>
        {
            order.Add(call.Path);
            return new ApiReply(200, "application/json", "{}");
        });
        var outcome = await Tools.ExecuteAsync(
            "async () => { paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_a' }); await paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_b' }); paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: {}, confirm: true }); return 'done'; }",
            dispatcher,
            Ct);
        Assert.Equal("\"done\"", outcome.Text);
        Assert.Equal(["/api/v1/hosts/host_a", "/api/v1/hosts/host_b", "/api/v1/hosts"], order);
        Assert.Contains("1 request(s) were still pending", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAnswerIsParsedJsonOrTextOrNullAndAFailedDispatchSaysNothing()
    {
        ScriptedDispatcher answers = new(call => call.Path switch
        {
            "/api/v1/hosts" => new ApiReply(200, "application/problem+json; charset=utf-8", "{\"title\":\"x\"}"),
            "/api/v1/ca" => new ApiReply(200, "text/plain", "plain text"),
            "/api/v1/hosts/host_a" => new ApiReply(204, null, null),
            "/api/v1/hosts/host_b" => new ApiReply(500, "application/json", "{not json"),
            _ => throw new InvalidOperationException("secret detail at Paduraksa.Admin.Mcp.Dispatcher line 42"),
        });
        var outcome = await Tools.ExecuteAsync(
            "async () => { const get = (p) => paduraksa.request({ method: 'GET', path: p }); const out = [await get('/api/v1/hosts'), await get('/api/v1/ca'), await get('/api/v1/hosts/host_a'), await get('/api/v1/hosts/host_b')]; try { await get('/api/v1/logs/recent'); } catch (e) { out.push(e.message); } return out; }",
            answers,
            Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal(
            "[{\"status\":200,\"body\":{\"title\":\"x\"}},{\"status\":200,\"body\":\"plain text\"},{\"status\":204,\"body\":null},{\"status\":500,\"body\":\"{not json\"},\"The request failed inside the server; nothing more is known.\"]",
            outcome.Text);
        Assert.DoesNotContain("secret detail", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithPrototypeShapedKeysIsDataAndPollutesNothing()
    {
        ScriptedDispatcher dispatcher = new(_ => new ApiReply(200, "application/json", "{\"__proto__\":{\"polluted\":true},\"constructor\":{\"prototype\":{\"polluted\":true}},\"a\":[{\"__proto__\":[1]}]}"));
        var outcome = await Tools.ExecuteAsync(
            "async () => { const r = await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); return { polluted: typeof ({}).polluted, own: Object.keys(r.body), samePrototype: Object.getPrototypeOf(r.body) === Object.prototype }; }",
            dispatcher,
            Ct);
        Assert.Equal("{\"polluted\":\"undefined\",\"own\":[\"__proto__\",\"constructor\",\"a\"],\"samePrototype\":true}", outcome.Text);
    }

    [Fact]
    public async Task TheScriptSeesOnlyTheRequestFunctionAndNothingOfTheParent()
    {
        var outcome = await Tools.ExecuteAsync(
            "async () => ({ keys: Object.keys(paduraksa), source: String(paduraksa.request), globals: Object.getOwnPropertyNames(globalThis).filter(k => /process|require|clr|System|importNamespace|fetch/.test(k)) })",
            new ScriptedDispatcher(),
            Ct);
        Assert.Equal("{\"keys\":[\"request\"],\"source\":\"function request() { [native code] }\",\"globals\":[]}", outcome.Text);
    }

    [Fact]
    public async Task ABodyTooDeepIsRefusedInTheScriptWithTheLimitItBroke()
    {
        ScriptedDispatcher dispatcher = new();
        var outcome = await Tools.ExecuteAsync(
            "async () => { const nest = (n) => n === 0 ? 1 : [nest(n - 1)]; const ok = await paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: nest(32) }); try { await paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: nest(33) }); return 'sent'; } catch (e) { return [ok.status, e.name + ': ' + e.message]; } }",
            dispatcher,
            Ct);
        Assert.Equal("[200,\"TypeError: The request body is nested deeper than 32 levels.\"]", outcome.Text);
        Assert.Single(dispatcher.Calls);
    }
}
