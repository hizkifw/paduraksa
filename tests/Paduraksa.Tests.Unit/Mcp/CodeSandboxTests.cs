using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

public sealed class CodeSandboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("async () => 1 + 1", "2")]
    [InlineData("async () => ({ a: [1, 'x', null], b: { c: true } })", "{\"a\":[1,\"x\",null],\"b\":{\"c\":true}}")]
    [InlineData("() => undefined", "null")]
    [InlineData("const k = 3; async () => k * 2", "6")]
    [InlineData("async () => { const r = await Promise.all([1, Promise.resolve(2)]); return r; }", "[1,2]")]
    [InlineData("async () => new Date(0)", "\"1970-01-01T00:00:00.000Z\"")]
    [InlineData("async () => ({ f() {}, u: undefined, s: Symbol('x'), n: NaN, i: Infinity, e: new Error('x') })", "{\"n\":null,\"i\":null,\"e\":{\"name\":\"Error\",\"message\":\"x\"}}")]
    [InlineData("async () => { const o = {}; o.self = o; return o; }", "{\"self\":\"[circular]\"}")]
    [InlineData("async () => JSON.stringify({ a: [1, { b: 2 }] }, null, 1)", "\"{\\n \\\"a\\\": [\\n  1,\\n  {\\n   \\\"b\\\": 2\\n  }\\n ]\\n}\"")]
    [InlineData("async () => [3, 1, 2].sort().join('-') + String([1, [2, 3]])", "\"1-2-31,2,3\"")]
    public async Task AScriptReturnsItsValueAsJson(string code, string json)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync(code, Ct);
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
    [InlineData("   ", "The code is empty")]
    public async Task AFailureNamesTheErrorAndWhereItWas(string code, string expected)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync(code, Ct);
        Assert.True(outcome.IsError);
        Assert.StartsWith(expected, outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Paduraksa", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeLongerThanTheLimitIsRefusedBeforeItIsParsed()
    {
        var outcome = await McpTestSpec.Tools(new SandboxLimits { MaxCodeChars = 50 }).SearchAsync("async () => '" + new string('x', 100) + "'", Ct);
        Assert.True(outcome.IsError);
        Assert.Contains("the limit is 50", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("typeof require")]
    [InlineData("typeof process")]
    [InlineData("typeof fetch")]
    [InlineData("typeof setTimeout")]
    [InlineData("typeof setInterval")]
    [InlineData("typeof console")]
    [InlineData("typeof Buffer")]
    [InlineData("typeof module")]
    [InlineData("typeof importScripts")]
    [InlineData("typeof XMLHttpRequest")]
    [InlineData("typeof WebSocket")]
    [InlineData("typeof Deno")]
    [InlineData("typeof ArrayBuffer")]
    [InlineData("typeof Uint8Array")]
    [InlineData("typeof SharedArrayBuffer")]
    [InlineData("typeof Atomics")]
    [InlineData("typeof Proxy")]
    [InlineData("typeof Intl")]
    [InlineData("typeof BigInt")]
    [InlineData("typeof WebAssembly")]
    [InlineData("typeof structuredClone")]
    [InlineData("typeof paduraksa")]
    public async Task NothingOutsideTheStandardLibraryIsReachable(string probe)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {probe}", Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("\"undefined\"", outcome.Text);
    }

    [Fact]
    public async Task TheGlobalScopeHoldsTheStandardLibraryAndTheToolsOneGlobal()
    {
        var outcome = await McpTestSpec.Tools().SearchAsync("async () => Object.getOwnPropertyNames(globalThis).sort()", Ct);
        Assert.False(outcome.IsError, outcome.Text);
        foreach (var forbidden in new[] { "process", "require", "console", "fetch", "Buffer", "ArrayBuffer", "Proxy", "Intl", "BigInt", "setTimeout", "paduraksa", "clr", "System", "importNamespace" })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", outcome.Text, StringComparison.Ordinal);
        }

        Assert.Contains("\"spec\"", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("\"JSON\"", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this.constructor.constructor('return process')()")]
    [InlineData("(async () => {}).constructor('return process')()")]
    [InlineData("Object.getPrototypeOf(async function () {}).constructor('return 1')()")]
    [InlineData("[].constructor.constructor('return globalThis.process')()")]
    [InlineData("Function('return this')()")]
    [InlineData("new Function('return 1')()")]
    [InlineData("eval('1 + 1')")]
    [InlineData("(0, eval)('process')")]
    public async Task CodeBuiltFromAStringCannotRun(string expression)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {expression}", Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.Contains("String compilation has been disabled", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1n")]
    [InlineData("2n ** 64n")]
    [InlineData("1n << 1000000000n")]
    [InlineData("0x10n")]
    public async Task BigIntLiteralsAreRefusedBecauseTheirArithmeticCannotBeBounded(string expression)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {expression}", Ct);
        Assert.True(outcome.IsError);
        Assert.StartsWith("BigInt is not available", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'a'.repeat(60000).replace(/a/g, \"$'\")")]
    [InlineData("'a'.repeat(60000).replace(/a/g, '$`')")]
    [InlineData("'a'.repeat(60000).replaceAll('a', \"$'\")")]
    [InlineData("'abc'.replace('b', 'x$$y$'+\"'\")")]
    [InlineData("'abc'.replace('b', { toString() { return \"$'\"; } })")]
    [InlineData("/a/g[Symbol.replace]('a'.repeat(60000), \"$'\")")]
    [InlineData("RegExp.prototype[Symbol.replace].call(/a/g, 'a'.repeat(60000), '$`')")]
    public async Task AReplacementThatExpandsToTheTextAroundTheMatchIsRefused(string expression)
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {expression}.length", Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.StartsWith("TypeError: A replacement may not use $' or $`", outcome.Text, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("'abc'.replace('b', '[$&|$`x]'.slice(0, 4) + '$&')", "\"a[b|bc\"")]
    [InlineData("'abc'.replace(/(b)/, '<$1$&$$>')", "\"a<bb$>c\"")]
    [InlineData("'aXb'.replace(/(?<k>X)/, '<$<k>>')", "\"a<X>b\"")]
    [InlineData("'a-b-c'.replaceAll('-', () => '$`')", "\"a$`b$`c\"")]
    [InlineData("'abc'.replace('b', $s => $s + '$\\'')", "\"ab$'c\"")]
    public async Task TheReplacementsThatOnlyPartitionTheInputStayAndAFunctionIsNotExpanded(string expression, string expected)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {expression}", Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal(expected, outcome.Text);
    }

    [Fact]
    public async Task AFunctionReplacementThatReturnsALongStringPerMatchIsStoppedAtTheStringLimit()
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools().SearchAsync(
            "async () => { const big = 'y'.repeat(1e6); return 'a'.repeat(60000).replace(/a/g, () => big).length; }",
            Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.StartsWith("RangeError: Invalid string length", outcome.Text, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("'x'.repeat('10000000')")]
    [InlineData("'x'.repeat({ valueOf() { return 1e7; } })")]
    [InlineData("String.prototype.repeat.call(new String('x'), 5e6)")]
    [InlineData("String.prototype.repeat.call({ toString() { return 'x'.repeat(1e6); } }, 10)")]
    [InlineData("'x'.padStart('10000000')")]
    [InlineData("'x'.padEnd(new Number(1e7))")]
    [InlineData("String.prototype.padStart.call(new String('x'), 1e7, 'ab')")]
    [InlineData("'x'.repeat(1e6).replace('x', 'yyyyy'.repeat(1e6))")]
    [InlineData("String.prototype.replace.call(new String('x'.repeat(1e6)), /x/g, new String('yyyyy'))")]
    [InlineData("''.concat(...new Array(5000).fill('x'.repeat(1e6)))")]
    [InlineData("RegExp.prototype[Symbol.replace].call(/x/g, 'x'.repeat(1e6), 'yyyyy'.repeat(1e6))")]
    public async Task ACountThatIsNotANumberOrAReceiverThatIsNotAStringIsJudgedLikeThePrimitive(string expression)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {expression}.length", Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.StartsWith("RangeError: Invalid string length", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoercedArgumentsStillWorkAndAnObjectSToStringRunsOnce()
    {
        var outcome = await McpTestSpec.Tools().SearchAsync(
            "async () => { let n = 0; const o = { toString() { n++; return 'ab'; } }; return [String.prototype.repeat.call(o, '2'), 'x'.padStart('3', 0), n, 'a'.repeat(-0), 'abc'.padEnd(5.9, '-')]; }",
            Ct);
        Assert.False(outcome.IsError, outcome.Text);
        Assert.Equal("[\"abab\",\"00x\",1,\"\",\"abc--\"]", outcome.Text);
        Assert.StartsWith("RangeError", (await McpTestSpec.Tools().SearchAsync("async () => 'x'.repeat(-1)", Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("RangeError", (await McpTestSpec.Tools().SearchAsync("async () => 'x'.repeat(Infinity)", Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeepFlatIsBoundedLikeTheRest()
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools().SearchAsync("async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.flat(Infinity).length; }", Ct);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(!outcome.IsError ? outcome.Text == "0" : outcome.Text.Contains("depth", StringComparison.Ordinal), outcome.Text);
    }

    [Theory]
    [InlineData("var f = function () { return 1; }; for (var i = 0; i < 90000; i++) f = f.bind(null); return f();")]
    [InlineData("var f = function () { return 1; }; for (var i = 0; i < 90000; i++) f = f.bind(null); return new f();")]
    [InlineData("var o = {}; for (var i = 0; i < 90000; i++) o = Object.create(o); return o.x;")]
    public async Task NativeRecursionOverAChainTheScriptBuiltEndsAsAResultAndNeverAsAStackOverflow(string body)
    {
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {{ {body} }}", Ct);
        // Either the chain was walked without recursion or the stack guard turned it into a catchable error: the process is still here.
        Assert.True(!outcome.IsError || outcome.Text.Contains("call stack", StringComparison.OrdinalIgnoreCase) || outcome.Text.Contains("allocated", StringComparison.Ordinal), outcome.Text);
    }

    [Fact]
    public async Task TheClocksAreReadOftenEnoughThatARegexLoopCannotRunFarPastTheScriptTime()
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools(new SandboxLimits { ScriptTime = TimeSpan.FromSeconds(2) }).SearchAsync(
            "async () => { for (let i = 0; i < 100; i++) { /(a+)+$/.test('a'.repeat(40) + 'b'); } }",
            Ct);
        Assert.True(outcome.IsError);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "it took " + (DateTime.UtcNow - started));
    }

    [Fact]
    public async Task AScriptOfCheapStatementsStillRunsAMillionStatementsAMinuteFast()
    {
        var outcome = await McpTestSpec.Tools(new SandboxLimits { MaxStatements = 1_000_000, ScriptTime = TimeSpan.FromSeconds(5), MaxAllocatedBytes = long.MaxValue })
            .SearchAsync("async () => { let n = 0; for (let i = 0; i < 300000; i++) { n += i; } return n; }", Ct);
        Assert.False(outcome.IsError, outcome.Text);
    }

    [Fact]
    public async Task NothingIsKeptFromOneCallToTheNext()
    {
        var tools = McpTestSpec.Tools();
        var first = await tools.SearchAsync("async () => { globalThis.leak = 1; Array.prototype.poison = 2; spec.paths = null; Object.prototype.polluted = true; return 1; }", Ct);
        Assert.False(first.IsError, first.Text);
        var second = await tools.SearchAsync("async () => [typeof leak, typeof [].poison, typeof spec.paths, typeof ({}).polluted]", Ct);
        Assert.Equal("[\"undefined\",\"undefined\",\"object\",\"undefined\"]", second.Text);
    }

    [Theory]
    [InlineData("async () => { for (;;) { } }", "statements")]
    [InlineData("async () => { while (true) { let x = 1; } }", "statements")]
    [InlineData("async () => { const f = () => f(); return f(); }", "deep")]
    [InlineData("async () => { function f(n) { return f(n + 1) + 1; } return f(0); }", "deep")]
    [InlineData("async () => new Array(1e9)", "array")]
    [InlineData("async () => new Array(100001).fill(0)", "array")]
    [InlineData("async () => 'x'.repeat(1e8)", "Invalid string length")]
    [InlineData("async () => 'x'.padStart(1e9)", "Invalid string length")]
    [InlineData("async () => { let s = 'x'; for (let i = 0; i < 40; i++) s += s; return s.length; }", "allocated")]
    [InlineData("async () => 'x'.repeat(1e6).replaceAll('x', 'y'.repeat(1e3))", "Invalid string length")]
    [InlineData("async () => new Array(50000).fill('x'.repeat(1000)).join('')", "Invalid string length")]
    [InlineData("async () => { let o = {}; for (let i = 0; i < 24; i++) o = { a: o, b: o }; return JSON.stringify(o); }", "Invalid string length|allocated")]
    [InlineData("async () => { const a = []; for (let i = 0; i < 90000; i++) a.push({ k: 'v'.repeat(500) + i }); return a.length; }", "allocated")]
    [InlineData("async () => { const f = () => Promise.resolve().then(f); f(); return 1; }", "allocated")]
    [InlineData("async () => { for (let i = 0; i < 1e6; i++) Promise.resolve(i).then(() => { }); return 1; }", "allocated")]
    [InlineData("async () => /(a+)+$/.test('a'.repeat(40) + 'b')", "regular expression")]
    public async Task ARunawayScriptEndsWithTheLimitItHit(string code, string expected)
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools().SearchAsync(code, Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.Contains(expected.Split('|'), alternative => outcome.Text.Contains(alternative, StringComparison.Ordinal));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "it took " + (DateTime.UtcNow - started));
    }

    [Theory]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return String(a);")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.join();")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return `${a}`;")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a < 1;")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return [a, a].sort();")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return ({})[a];")]
    [InlineData("var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.toLocaleString();")]
    public async Task DeeplyNestedDataCannotRecurseTheProcessIntoAStackOverflow(string body)
    {
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools().SearchAsync($"async () => {{ {body} }}", Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.Contains("nesting depth", outcome.Text, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReturningDeepOrSharedStructuresIsBoundedByTheOutputBudgetNotByTheirSize()
    {
        var deep = await McpTestSpec.Tools().SearchAsync("async () => { var a = []; for (var i = 0; i < 100000; i++) a = [a]; return a; }", Ct);
        Assert.False(deep.IsError, deep.Text);
        Assert.Contains("[too deep]", deep.Text, StringComparison.Ordinal);

        var shared = await McpTestSpec.Tools().SearchAsync("async () => { var o = {}; for (var i = 0; i < 40; i++) o = { a: o, b: o }; return o; }", Ct);
        Assert.False(shared.IsError, shared.Text);
        Assert.NotNull(shared.Note);
        Assert.True(shared.Text.Length <= 64 * 1024);
    }

    [Fact]
    public async Task AResultOverTheCapIsCutAndSaysSo()
    {
        var outcome = await McpTestSpec.Tools().SearchAsync("async () => Array.from({ length: 5000 }, (_, i) => ({ index: i, text: 'row ' + i }))", Ct);
        Assert.False(outcome.IsError);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(outcome.Text) <= 64 * 1024, "the text is over the cap");
        Assert.Contains("64 KiB", outcome.Note, StringComparison.Ordinal);
        Assert.StartsWith("[{\"index\":0,\"text\":\"row 0\"}", outcome.Text, StringComparison.Ordinal);

        var small = await McpTestSpec.Tools(new SandboxLimits { MaxOutputBytes = 1000 }).SearchAsync("async () => 'é'.repeat(2000)", Ct);
        Assert.NotNull(small.Note);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(small.Text) <= 1000);
    }

    [Fact]
    public async Task AScriptThatOutrunsItsOwnTimeIsStopped()
    {
        var outcome = await McpTestSpec.Tools(new SandboxLimits { MaxStatements = int.MaxValue, MaxAllocatedBytes = long.MaxValue, ScriptTime = TimeSpan.FromMilliseconds(300) })
            .SearchAsync("async () => { let n = 0; for (;;) { n++; } }", Ct);
        Assert.True(outcome.IsError);
        Assert.Contains("of its own time", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowRequestCountsAgainstTheTotalTimeAndNotAgainstTheScriptTime()
    {
        SlowDispatcher dispatcher = new(TimeSpan.FromMilliseconds(1200));
        var tools = McpTestSpec.Tools(new SandboxLimits { ScriptTime = TimeSpan.FromMilliseconds(500), TotalTime = TimeSpan.FromSeconds(10) });
        var done = await tools.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", dispatcher, Ct);
        Assert.False(done.IsError, done.Text);
        Assert.Equal("200", done.Text);

        var capped = McpTestSpec.Tools(new SandboxLimits { TotalTime = TimeSpan.FromMilliseconds(300) });
        var late = await capped.ExecuteAsync(
            "async () => { await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); let n = 0; for (let i = 0; i < 5e4; i++) { n += i; } return n; }",
            dispatcher,
            Ct);
        Assert.True(late.IsError, late.Text);
        Assert.Contains("in all", late.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestThatOutlastsTheTotalTimeIsCancelledAndTheCallEndsWithThatLimit()
    {
        SlowDispatcher hung = new(TimeSpan.FromMinutes(5));
        var started = DateTime.UtcNow;
        var outcome = await McpTestSpec.Tools(new SandboxLimits { TotalTime = TimeSpan.FromMilliseconds(400) })
            .ExecuteAsync("async () => await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })", hung, Ct);
        Assert.True(outcome.IsError);
        Assert.Contains("in all", outcome.Text, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task OnlyAsManyScriptsRunAtOnceAsTheLimitAllows()
    {
        var tools = McpTestSpec.Tools(new SandboxLimits { MaxConcurrentRuns = 1 });
        SlowDispatcher slow = new(TimeSpan.FromMilliseconds(600));
        var first = tools.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", slow, Ct);
        await Task.Delay(150, Ct);
        var second = await tools.SearchAsync("async () => 1", Ct);
        Assert.True(second.IsError);
        Assert.Contains("Other scripts are running", second.Text, StringComparison.Ordinal);
        Assert.False((await first).IsError);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
    }

    [Fact]
    public async Task ACallerWhoGoesAwayStopsTheScript()
    {
        using CancellationTokenSource gone = new();
        SlowDispatcher slow = new(TimeSpan.FromSeconds(30));
        var running = McpTestSpec.Tools().ExecuteAsync("async () => await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })", slow, gone.Token);
        await Task.Delay(200, Ct);
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task SearchReadsTheSpecWithItsReferencesResolvedAndSharedWhereTheyAreTheSame()
    {
        var outcome = await McpTestSpec.Tools().SearchAsync(
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

    private sealed class SlowDispatcher(TimeSpan delay) : IApiDispatcher
    {
        public async ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new ApiReply(200, "application/json", "{}");
        }
    }
}
