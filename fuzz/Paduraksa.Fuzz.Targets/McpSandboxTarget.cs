using System.Diagnostics;
using System.Text;

using Paduraksa.Mcp;

namespace Paduraksa.Fuzz;

/// <summary>
/// A model's JavaScript through the MCP sandbox (PLAN §11.2, PLAN §15): the input is a tool byte (even: <c>search</c> over a small API description, odd:
/// <c>execute</c> against a recording stub) and the script as UTF-8. Properties, on every input: the tool returns and never throws; it returns within the time
/// the limits allow (a script stuck in one native call is the failure this looks for: the call would be abandoned, or the next one told that others run); the
/// process allocated a bounded amount; a successful result is at most the output cap and an error is a short message that names no type of this process and
/// holds no stack; nothing outside the globals is reachable (a sentinel in the environment never appears in a result, nor does a type name or a path); the
/// sandbox reported no unexpected exception (one is a bug in a native the guards do not cover, contained here but worth a look); and what the stub received
/// is what the policy allows: a path under the API, a known method, at most the request limit, nothing refused, and a write only when the script says
/// <c>confirm</c> (or as a dry run).
/// </summary>
internal static class McpSandboxTarget
{
    private const string Sentinel = "PADURAKSA_FUZZ_SANDBOX_SENTINEL_7f3a91";

    private const string OpenApi = """
        {
          "openapi": "3.1.1",
          "info": { "title": "Fuzz API", "version": "v1" },
          "paths": {
            "/api/v1/hosts": {
              "get": { "operationId": "ListHosts", "summary": "List hosts", "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Host" } } } } } } },
              "post": { "operationId": "CreateHost", "parameters": [ { "name": "dryRun", "in": "query", "schema": { "type": "boolean" } } ], "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Host" } } } }, "responses": { "201": { "description": "created" } } }
            },
            "/api/v1/hosts/{id}": {
              "get": { "operationId": "GetHost", "parameters": [ { "$ref": "#/components/parameters/Id" } ], "responses": { "200": { "description": "ok" } } },
              "put": { "operationId": "ReplaceHost", "parameters": [ { "$ref": "#/components/parameters/Id" }, { "name": "dryRun", "in": "query", "schema": { "type": "boolean" } } ], "responses": { "200": { "description": "ok" } } },
              "delete": { "operationId": "DeleteHost", "parameters": [ { "$ref": "#/components/parameters/Id" } ], "responses": { "204": { "description": "gone" } } }
            },
            "/api/v1/certificates/{id}/renew": { "post": { "operationId": "RenewCertificate", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/simulate": { "post": { "operationId": "Simulate", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/tokens": { "get": { "operationId": "ListTokens", "responses": { "200": { "description": "ok" } } }, "post": { "operationId": "IssueToken", "responses": { "201": { "description": "ok" } } } },
            "/api/v1/logs/tail": { "get": { "operationId": "StreamAccessLog", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/logs/recent": { "get": { "operationId": "RecentAccessLog", "responses": { "200": { "description": "ok" } } } }
          },
          "components": {
            "parameters": { "Id": { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } },
            "schemas": {
              "Host": { "type": "object", "properties": { "name": { "type": "string" }, "settings": { "$ref": "#/components/schemas/Settings" }, "tree": { "$ref": "#/components/schemas/Node" } } },
              "Settings": { "type": "object", "properties": { "hsts": { "type": "boolean" } } },
              "Node": { "type": "object", "properties": { "children": { "type": "array", "items": { "$ref": "#/components/schemas/Node" } } } }
            },
            "securitySchemes": { "bearer": { "type": "http", "scheme": "bearer" } }
          }
        }
        """;

    private static readonly string[] Forbidden =
    [
        Sentinel, "System.", "Jint.", "Acornima.", "Paduraksa.", "Microsoft.", ".cs:", "/home/", "/tmp/", "Exception",
    ];

    /// <summary>The production limits, with the times cut so that a run is a fraction of a second; every cap that shapes a property (output, requests, statements, memory, stack) is the production one.</summary>
    private static readonly SandboxLimits Limits = new() { ScriptTime = TimeSpan.FromMilliseconds(250), TotalTime = TimeSpan.FromSeconds(2) };

    private static readonly List<string> Unexpected = [];

    private static CodeModeTools? s_tools;

    public static FuzzTarget Target { get; } = new(
        "mcp-sandbox",
        "a model's JavaScript through the MCP sandbox (search over a spec, execute against a stub client)",
        Run,
        Seeds);

    private static CodeModeTools Tools()
    {
        Environment.SetEnvironmentVariable(Sentinel, Sentinel);
        return s_tools ??= CodeModeTools.Create(Encoding.UTF8.GetBytes(OpenApi), Limits, ex => Unexpected.Add(ex.GetType().Name + ": " + ex.Message));
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var tools = Tools();
        var execute = (input[0] & 1) == 1;
        var code = Encoding.UTF8.GetString(input[1..]);
        Unexpected.Clear();
        RecordingDispatcher dispatcher = new();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var clock = Stopwatch.StartNew();
        SandboxOutcome outcome;
        try
        {
            outcome = (execute ? tools.ExecuteAsync(code, dispatcher, CancellationToken.None) : tools.SearchAsync(code, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new PropertyViolationException("the tool threw", ex);
        }

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;

        Checks.That(clock.Elapsed < Limits.TotalTime + TimeSpan.FromSeconds(3), $"the call took {clock.Elapsed}");
        Checks.That(allocated < 768L * 1024 * 1024, $"the process allocated {allocated / (1024 * 1024)} MiB for one call");
        Checks.That(!outcome.Text.StartsWith("Other scripts are running", StringComparison.Ordinal), "an earlier script still holds the sandbox: it did not stop");
        Checks.That(!outcome.Text.Contains("did not stop in time", StringComparison.Ordinal), "the script did not stop in time and was abandoned");
        Checks.That(Unexpected.Count == 0, "the sandbox failed unexpectedly: " + string.Join("; ", Unexpected));

        var bytes = Encoding.UTF8.GetByteCount(outcome.Text);
        if (outcome.IsError)
        {
            Checks.That(outcome.Text.Length <= 600, $"an error message of {outcome.Text.Length} characters");
        }
        else
        {
            Checks.That(bytes <= Limits.MaxOutputBytes, $"a result of {bytes} bytes is over the cap");
        }

        foreach (var marker in Forbidden)
        {
            Checks.That(!outcome.Text.Contains(marker, StringComparison.Ordinal) && (outcome.Note?.Contains(marker, StringComparison.Ordinal) != true), $"the result names '{marker}'");
        }

        Checks.That(outcome.Requests <= Limits.MaxRequests + 1, $"{outcome.Requests} requests");
        Checks.That(dispatcher.Calls.Count <= Limits.MaxRequests, $"{dispatcher.Calls.Count} requests were dispatched");
        var saysConfirm = code.Contains("confirm", StringComparison.Ordinal);
        foreach (var call in dispatcher.Calls)
        {
            var decision = tools.Policy.Decide(call.Method, call.Path);
            Checks.That(decision.Kind is RequestKind.Read or RequestKind.Write, $"{call.Method} {call.Path} was dispatched and the policy says {decision.Kind}");
            Checks.That(call.Path.StartsWith("/api/v1/", StringComparison.Ordinal) && call.Path.All(c => c is > ' ' and < '\u007f'), $"the path '{call.Path}' is not a plain API path");
            Checks.That(call.Query.Count <= 64 && call.Query.All(q => q.Value.Length <= 1024), "an oversized query reached the dispatcher");
            Checks.That(call.BodyJson is null || Encoding.UTF8.GetByteCount(call.BodyJson) <= Limits.MaxRequestBodyBytes, "an oversized body reached the dispatcher");
            Checks.That(call.IfMatch is null || (call.IfMatch.Length <= 202 && call.IfMatch.All(c => c is > ' ' and < '\u007f')), "a bad entity tag reached the dispatcher");
            if (decision.Kind == RequestKind.Write)
            {
                var dryRun = call.Query.Any(q => q.Key == "dryRun" && q.Value == "true");
                Checks.That(dryRun || saysConfirm, $"{call.Method} {call.Path} was sent as a write and the script never says confirm");
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        var index = 0;
        foreach (var (execute, script) in Scripts)
        {
            yield return ($"{index++:00}-{(execute ? "execute" : "search")}", [(byte)(execute ? 1 : 0), .. Encoding.UTF8.GetBytes(script)]);
        }
    }

    /// <summary>Well-formed starting points: ordinary scripts of both tools, then the shapes the sandbox exists for (runaway loops and recursion, allocation, escapes, amplifiers, nesting, promise floods).</summary>
    private static readonly (bool Execute, string Script)[] Scripts =
    [
        (false, "async () => Object.keys(spec.paths).length"),
        (false, "async () => Object.entries(spec.paths).flatMap(([p, ops]) => Object.entries(ops).map(([m, op]) => m.toUpperCase() + ' ' + p + ' ' + op.summary))"),
        (false, "async () => spec.paths['/api/v1/hosts'].post.requestBody.content['application/json'].schema.properties.tree"),
        (false, "async () => JSON.stringify(spec.components.schemas.Host, null, 2)"),
        (true, "async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts', query: { limit: 5 } })).body"),
        (true, "async () => { const r = await Promise.all([paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_a' }), paduraksa.request({ method: 'GET', path: '/api/v1/hosts/host_b' })]); return r.map(x => x.status); }"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: { name: 'a' } })"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: { name: 'a' }, confirm: true })"),
        (true, "async () => paduraksa.request({ method: 'PUT', path: '/api/v1/hosts/host_a', body: {}, ifMatch: '\"3\"', confirm: true })"),
        (true, "async () => paduraksa.request({ method: 'DELETE', path: '/api/v1/hosts/host_a' })"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/api/v1/certificates/cert_a/renew' })"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: { host: 'a' } })"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/api/v1/tokens' })"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/api/v1/logs/tail' })"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/api/v1/hosts/../tokens' })"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/API/V1/TOKENS' })"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/api/v1/hosts?x=1#y', query: { dryRun: 'false' } })"),
        (true, "async () => { for (let i = 0; i < 60; i++) { await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); } }"),
        (true, "async () => { paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: {}, confirm: true }); return 1; }"),
        (true, "async () => paduraksa.request({ method: 'GET', path: '/api/v1/hosts', headers: { Authorization: 'x' } })"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: { a: 'x'.repeat(1e6) }, confirm: true })"),
        (true, "async () => paduraksa.request.call(null, { method: 'GET', path: '/api/v1/hosts' })"),
        (false, "async () => { for (;;) { } }"),
        (false, "async () => { while (true) { let x = 1; } }"),
        (false, "async () => { const f = () => f(); return f(); }"),
        (false, "async () => { function f(n) { return f(n + 1) + 1; } return f(0); }"),
        (false, "async () => new Array(1e9)"),
        (false, "async () => 'x'.repeat(2 ** 29)"),
        (false, "async () => { let s = 'x'; for (let i = 0; i < 40; i++) s += s; return s.length; }"),
        (false, "async () => 'x'.padStart(1e9)"),
        (false, "async () => 'x'.repeat(1e6).replaceAll('x', 'y'.repeat(1e3))"),
        (false, "async () => new Array(50000).fill('x'.repeat(1000)).join('')"),
        (false, "async () => { const a = []; for (let i = 0; i < 90000; i++) a.push({ k: 'v'.repeat(500) + i }); return a.length; }"),
        (false, "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return String(a); }"),
        (false, "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return JSON.stringify(a); }"),
        (false, "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a; }"),
        (false, "async () => { var o = {}; for (var i = 0; i < 40; i++) o = { a: o, b: o }; return o; }"),
        (false, "async () => { var o = {}; for (var i = 0; i < 40; i++) o = { a: o, b: o }; return JSON.stringify(o); }"),
        (false, "async () => { const f = () => Promise.resolve().then(f); f(); return 1; }"),
        (false, "async () => { for (let i = 0; i < 1e6; i++) Promise.resolve(i).then(() => { }); return 1; }"),
        (false, "async () => { await new Promise(() => { }); }"),
        (false, "async () => /(a+)+$/.test('a'.repeat(40) + 'b')"),
        (false, "async () => 'a'.repeat(100000).replace(/(.*)*x/, '')"),
        (false, "async () => 1n << 1000000000n"),
        (false, "async () => BigInt(5)"),
        (false, "async () => [typeof require, typeof process, typeof fetch, typeof setTimeout, typeof console, typeof globalThis, typeof Proxy, typeof Intl, typeof ArrayBuffer].join()"),
        (false, "async () => this.constructor.constructor('return process')()"),
        (false, "async () => (function () { return this; })().constructor.constructor('return process')()"),
        (false, "async () => Function('return process.env')()"),
        (false, "async () => eval('process')"),
        (false, "async () => Object.getPrototypeOf(async () => { }).constructor('return process')()"),
        (false, "async () => Object.getOwnPropertyNames(globalThis)"),
        (false, "async () => Reflect.ownKeys(globalThis).map(String)"),
        (false, "async () => { const out = []; for (const k of Object.getOwnPropertyNames(globalThis)) { try { out.push(k + ':' + typeof globalThis[k]); } catch (e) { } } return out; }"),
        (false, "async () => globalThis.constructor.name + Object.getPrototypeOf(globalThis).constructor.name"),
        (false, "async () => JSON.stringify(Object.getOwnPropertyDescriptors(globalThis))"),
        (false, "async () => { const e = new Error('x'); return String(e.stack); }"),
        (false, "async () => { try { null.x; } catch (e) { return e.stack; } }"),
        (false, "async () => { throw new Error(Object.keys(spec).join()); }"),
        (false, "async () => ({ get x() { throw new Error('getter'); } })"),
        (false, "async () => ({ toJSON() { return 1; } })"),
        (false, "async () => ({ [Symbol.toPrimitive]() { return 'x'; } }) + ''"),
        (false, "async () => [1, 2, 3].map(x => x * 2).sort((a, b) => b - a).join()"),
        (false, "async () => Array.from({ length: 1e9 })"),
        (false, "async () => 'abc'.localeCompare('abd') + new Date(0).toLocaleString() + (1234.5).toLocaleString()"),
        (false, "async () => 0.1 + 0.2 + Math.max(...new Array(100000).fill(1))"),
        (false, "async () => { const a = []; a.length = 4294967295; return a.sort().length; }"),
        (false, "async () => { const m = new Map(); for (let i = 0; i < 1e5; i++) m.set(i, i); return m.size; }"),
        (false, "async () => { let s = ''; for (let i = 0; i < 1e5; i++) s += 'abcdefghij'; return s.length; }"),
        (false, "async () => JSON.parse('[' .repeat(10000) + ']'.repeat(10000))"),
        (false, "async () => new Date(NaN)"),
        (false, "async () => ({ e: new Error('x'), d: new Date(0), f() { }, u: undefined, s: Symbol('x'), n: NaN, i: Infinity })"),
        (false, "async () => { const o = {}; o.self = o; return o; }"),
        (false, "async () => { let x = ; }"),
        (false, "async () => { return "),
        (false, "\u0000￿\ud800 async () => 1"),
        (false, "return 1"),
        (false, "async () => { const s = 'a'.repeat(60000); return s.replace(/a/g, \"$'\").length; }"),
        (false, "async () => { const s = 'a'.repeat(60000); return s.replace(/a/g, '$`').length; }"),
        (false, "async () => 'a'.repeat(60000).replaceAll('a', \"$'\").length"),
        (false, "async () => /a/g[Symbol.replace]('a'.repeat(60000), \"$'\").length"),
        (false, "async () => RegExp.prototype[Symbol.replace].call(/a/g, 'a'.repeat(60000), '$`').length"),
        (false, "async () => 'a-b'.replace('-', '$&$1$<n>$$').length"),
        (false, "async () => { const big = 'y'.repeat(1e6); return 'a'.repeat(60000).replace(/a/g, () => big).length; }"),
        (false, "async () => 'x'.repeat('10000000').length"),
        (false, "async () => String.prototype.repeat.call(new String('x'), 5e6).length"),
        (false, "async () => 'x'.padStart({ valueOf() { return 1e9; } }).length"),
        (false, "async () => String.prototype.padEnd.call(new String('x'), '1e7').length"),
        (false, "async () => ''.concat(...new Array(5000).fill('x'.repeat(1e6))).length"),
        (false, "async () => { let n = 0; const o = { toString() { n++; return 'ab'; } }; return [String.prototype.repeat.call(o, '2'), n]; }"),
        (false, "async () => { var f = function () { return 1; }; for (var i = 0; i < 90000; i++) f = f.bind(null); return f(); }"),
        (false, "async () => { var f = function () { return 1; }; for (var i = 0; i < 90000; i++) f = f.bind(null); return new f(); }"),
        (false, "async () => { var o = {}; for (var i = 0; i < 90000; i++) o = Object.create(o); return o.x; }"),
        (false, "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.flat(Infinity).length; }"),
        (false, "async () => { for (let i = 0; i < 100; i++) { /(a+)+$/.test('a'.repeat(40) + 'b'); } }"),
        (true, "async () => paduraksa.request({ method: 'POST', path: '/mcp', body: {}, confirm: true })"),
        (true, "async () => { try { await paduraksa.request({ method: 'GET', path: '/api/v1/tokens' }); } catch (e) { return e.message; } }"),
        (true, "async () => Object.getOwnPropertyNames(paduraksa).concat(Object.getOwnPropertyNames(paduraksa.request))"),
        (true, "async () => { const p = paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); return [typeof p.then, await p]; }"),
    ];

    private sealed class RecordingDispatcher : IApiDispatcher
    {
        public List<ApiCall> Calls { get; } = [];

        public ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return ValueTask.FromResult(request.Path.EndsWith("/hosts", StringComparison.Ordinal)
                ? new ApiReply(200, "application/json", "[{\"id\":\"host_a\",\"name\":\"a\",\"tree\":{\"children\":[]}}]", "\"3\"")
                : new ApiReply(200, "application/json", "{\"ok\":true}"));
        }
    }
}
