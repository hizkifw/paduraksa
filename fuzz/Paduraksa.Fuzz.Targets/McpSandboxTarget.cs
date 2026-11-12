using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Paduraksa.Mcp;

namespace Paduraksa.Fuzz;

/// <summary>
/// The MCP sandbox's parent, against a child that is not to be trusted (PLAN §11.2, §15). The first input byte picks the mode.
/// <list type="bullet">
/// <item><b>Frames</b> (three inputs in four, in process, fast, instrumented): the rest of the input is what a hostile child writes to its stdout, fed to the
/// parent's run protocol over a stub dispatcher. It may return an outcome or end the run as a breach or a protocol violation, and nothing else may happen:
/// no other exception; a bounded outcome (a result within the output cap, an error short and without control characters); at most the request limit
/// dispatched; and every request that reached the dispatcher is one the request policy allows, with a write only if the bytes say <c>confirm</c> or as a dry run.</item>
/// <item><b>Scripts</b> (one in four, when the harness can start a child, see <see cref="ChildVariable"/>): the rest is a script, run on a real child of an
/// uninstrumented copy of the harness. The properties are the parent's: it never throws, the call ends within the wall-time limit plus a margin, the answer is
/// bounded, and the pool holds at most one child. The script itself is not under test: a stock engine in a child that is killed on a breach is the design, so
/// crashes, hangs and exhaustion in there are expected and are what the seeds (the scripts that were once dangerous in process) show the parent absorbing.</item>
/// </list>
/// Without a child, a script input is read as frames, which keeps the corpus replayable in CI.
/// </summary>
internal static class McpSandboxTarget
{
    /// <summary>The driver assembly the harness starts as the child (<c>dotnet &lt;it&gt; mcp-sandbox</c>); <c>scripts/fuzz.sh</c> sets it to an uninstrumented publish.</summary>
    public const string ChildVariable = "PADURAKSA_FUZZ_CHILD";

    private const string OpenApi = """
        {
          "openapi": "3.1.1",
          "info": { "title": "Fuzz API", "version": "v1" },
          "paths": {
            "/api/v1/hosts": {
              "get": { "operationId": "ListHosts", "responses": { "200": { "description": "ok" } } },
              "post": { "operationId": "CreateHost", "parameters": [ { "name": "dryRun", "in": "query", "schema": { "type": "boolean" } } ], "responses": { "201": { "description": "created" } } }
            },
            "/api/v1/hosts/{id}": {
              "get": { "operationId": "GetHost", "responses": { "200": { "description": "ok" } } },
              "delete": { "operationId": "DeleteHost", "responses": { "204": { "description": "gone" } } }
            },
            "/api/v1/simulate": { "post": { "operationId": "Simulate", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/tokens": { "get": { "operationId": "ListTokens", "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    /// <summary>The run id the frames mode expects; the seeds carry it, so the fuzzer can mutate it into a wrong one, a missing one or an old one.</summary>
    private const string RunId = "fuzzrunid";

    private static readonly SandboxLimits FrameLimits = new() { MaxRequests = 10, TotalTime = TimeSpan.FromSeconds(2) };

    private static readonly SandboxLimits ScriptLimits = new()
    {
        ScriptTime = TimeSpan.FromSeconds(1),
        CpuTime = TimeSpan.FromSeconds(1),
        TotalTime = TimeSpan.FromSeconds(3),
        ChildRssBytes = 300L * 1024 * 1024,
        MaxRequests = 10,
    };

    private static readonly Lock Gate = new();
    private static RequestPolicy? s_policy;
    private static CodeModeTools? s_tools;

    public static FuzzTarget Target { get; } = new(
        "mcp-sandbox",
        "the MCP sandbox's parent against a hostile child: raw frames in process, and scripts through a real child",
        Run,
        Seeds);

    private static RequestPolicy Policy()
    {
        lock (Gate)
        {
            return s_policy ??= RequestPolicy.FromSpec(JsonDocument.Parse(OpenApi).RootElement.Clone());
        }
    }

    private static SandboxChildCommand? ChildCommand()
    {
        var path = Environment.GetEnvironmentVariable(ChildVariable);
        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Paduraksa.Fuzz.dll");
        }

        return File.Exists(path) ? SandboxChildCommand.ForAssembly(path) : null;
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var payload = input[1..];
        if ((input[0] & 3) == 0 && ChildCommand() is { } command)
        {
            Script(payload, command);
        }
        else
        {
            Frames(payload);
        }
    }

    private static void Frames(ReadOnlySpan<byte> input)
    {
        var hostile = input.ToArray();
        using MemoryStream fromChild = new(hostile);
        using MemoryStream toChild = new();
        RecordingDispatcher dispatcher = new();
        RequestBridge bridge = new(Policy(), dispatcher, FrameLimits);
        RunState state = new(RunId);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        RunProtocol.Exchange? exchange = null;
        try
        {
            exchange = RunProtocol.ExchangeAsync(fromChild, toChild, bridge, FrameLimits, state, _ => { }, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SandboxProtocolException or RunBreachException)
        {
            // the run ends: the parent kills the child and tells the caller a fixed message
        }
        catch (Exception ex)
        {
            throw new PropertyViolationException("the run protocol threw", ex);
        }

        if (exchange is { } done)
        {
            Bounded(done.Outcome);
        }

        Checks.That(state.Requests <= FrameLimits.MaxRequests + 1, $"{state.Requests} requests counted");
        Checks.That(dispatcher.Calls.Count <= FrameLimits.MaxRequests, $"{dispatcher.Calls.Count} requests dispatched");
        var says = Encoding.UTF8.GetString(hostile).Contains("confirm", StringComparison.Ordinal);
        foreach (var call in dispatcher.Calls)
        {
            var decision = Policy().Decide(call.Method, call.Path);
            Checks.That(decision.Kind is RequestKind.Read or RequestKind.Write, $"{call.Method} {call.Path} was dispatched and the policy says {decision.Kind}");
            Checks.That(call.Path.StartsWith("/api/v1/", StringComparison.Ordinal) && call.Path.All(c => c is > ' ' and < '\u007f'), $"the path '{call.Path}' is not plain");
            Checks.That(call.Query.Count <= 64 && call.Query.All(q => q.Value.Length <= 1024), "an oversized query reached the dispatcher");
            Checks.That(call.BodyJson is null || Encoding.UTF8.GetByteCount(call.BodyJson) <= FrameLimits.MaxRequestBodyBytes, "an oversized body reached the dispatcher");
            Checks.That(call.IfMatch is null || (call.IfMatch.Length <= 202 && call.IfMatch.All(c => c is > ' ' and < '\u007f')), "a bad entity tag reached the dispatcher");
            if (decision.Kind == RequestKind.Write)
            {
                Checks.That(call.Query.Any(q => q.Key == "dryRun" && q.Value == "true") || says, $"{call.Method} {call.Path} was sent as a write and the bytes never say confirm");
            }
        }

        // What the parent wrote back is frames the protocol allows, whatever the child sent.
        using MemoryStream written = new(toChild.ToArray());
        while (FrameCodec.Read(written, FrameCodec.MaxToChild) is { } frame)
        {
            using var message = Messages.Parse(frame);
            Checks.That(Messages.TypeOf(message) == "response", "the parent wrote a frame that is not a response");
        }
    }

    private static void Script(ReadOnlySpan<byte> input, SandboxChildCommand command)
    {
        CodeModeTools tools;
        lock (Gate)
        {
            tools = s_tools ??= CodeModeTools.Create(Encoding.UTF8.GetBytes(OpenApi), command, TimeSpan.FromMinutes(1), ScriptLimits);
        }

        var code = Encoding.UTF8.GetString(input);
        RecordingDispatcher dispatcher = new();
        var clock = Stopwatch.StartNew();
        SandboxOutcome outcome;
        try
        {
            outcome = tools.ExecuteAsync(code, dispatcher, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new PropertyViolationException("the parent threw", ex);
        }

        Checks.That(clock.Elapsed < ScriptLimits.TotalTime + TimeSpan.FromSeconds(4), $"the call took {clock.Elapsed}");
        Checks.That(!outcome.Text.StartsWith("Other scripts are running", StringComparison.Ordinal), "an earlier call still holds the pool");
        Checks.That(tools.Pool.ChildCount <= 1, $"{tools.Pool.ChildCount} children");
        Checks.That(dispatcher.Calls.Count <= ScriptLimits.MaxRequests, $"{dispatcher.Calls.Count} requests dispatched");
        Bounded(outcome);
    }

    private static void Bounded(SandboxOutcome outcome)
    {
        if (outcome.IsError)
        {
            Checks.That(outcome.Text.Length <= 700, $"an error of {outcome.Text.Length} characters");
            Checks.That(!outcome.Text.Any(char.IsControl), "an error with a control character");
        }
        else
        {
            Checks.That(Encoding.UTF8.GetByteCount(outcome.Text) <= SandboxLimits.Default.MaxOutputBytes, "a result over the output cap");
        }

        Checks.That(outcome.Note is null || (outcome.Note.Length <= 1500 && !outcome.Note.Any(char.IsControl)), "an unbounded note");
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, body.Length);
        body.CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        var index = 0;
        foreach (var script in Scripts)
        {
            yield return ($"{index++:00}-script", [0, .. Encoding.UTF8.GetBytes(script)]);
        }

        foreach (var (name, frames) in Streams)
        {
            yield return ($"{index++:00}-frames-{name}", [1, .. frames]);
        }
    }

    private static readonly (string Name, byte[] Frames)[] Streams =
    [
        ("result", Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"[1,2]","truncated":false}""")),
        ("error", Frame("""{"t":"result","rid":"fuzzrunid","ok":false,"error":"TypeError: x (line 1, column 2)"}""")),
        ("request-then-result", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}"""), Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"1","truncated":false}"""))),
        ("write-confirmed", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"POST\",\"path\":\"/api/v1/hosts\",\"body\":{},\"confirm\":true}"}"""), Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"1","truncated":false}"""))),
        ("write-dry-run", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"POST\",\"path\":\"/api/v1/hosts\",\"body\":{}}"}"""), Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"1","truncated":false}"""))),
        ("delete-no-confirm", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"DELETE\",\"path\":\"/api/v1/hosts/host_a\"}"}"""), Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"1","truncated":false}"""))),
        ("refused", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/tokens\"}"}"""), Frame("""{"t":"result","rid":"fuzzrunid","ok":true,"value":"1","truncated":false}"""))),
        ("path-tricks", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts/../tokens\"}"}"""), Frame("""{"t":"request","rid":"fuzzrunid","id":2,"options":"{\"method\":\"GET\",\"path\":\"/API/V1/TOKENS\"}"}"""), Frame("""{"t":"request","rid":"fuzzrunid","id":3,"options":"{\"method\":\"POST\",\"path\":\"/mcp\",\"confirm\":true}"}"""))),
        ("too-many-requests", Concat([.. Enumerable.Range(1, 12).Select(i => Frame($$"""{"t":"request","rid":"fuzzrunid","id":{{i}},"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}"""))])),
        ("ids-out-of-order", Frame("""{"t":"request","rid":"fuzzrunid","id":9,"options":"{}"}""")),
        ("bad-options", Concat(Frame("""{"t":"request","rid":"fuzzrunid","id":1,"options":"not json"}"""), Frame("""{"t":"request","rid":"fuzzrunid","id":2,"options":"{\"method\":1}"}"""))),
        ("unknown-type", Frame("""{"t":"surprise"}""")),
        ("not-json", Frame("this is not json")),
        ("no-type", Frame("""{"x":1}""")),
        ("oversized-length", [0x7f, 0xff, 0xff, 0xff, 1, 2, 3]),
        ("zero-length", [0, 0, 0, 0]),
        ("cut-frame", [0, 0, 0, 64, 123, 34, 116, 34]),
        ("long-error", Frame("{\"t\":\"result\",\"rid\":\"fuzzrunid\",\"ok\":false,\"error\":\"" + new string('x', 3000) + "\\u0007\\n\"}")),
        ("long-value", Frame("{\"t\":\"result\",\"rid\":\"fuzzrunid\",\"ok\":true,\"value\":\"" + new string('é', 60000) + "\",\"truncated\":true}")),
        ("deep-json", Frame("{\"t\":\"result\",\"rid\":\"fuzzrunid\",\"ok\":true,\"value\":\"x\",\"z\":" + new string('[', 200) + new string(']', 200) + "}")),
        ("empty", []),
    ];

    /// <summary>The scripts that were once dangerous in process, and ordinary ones: each ends as a result or as a fixed error, with the parent unharmed.</summary>
    private static readonly string[] Scripts =
    [
        "async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status",
        "async () => paduraksa.request({ method: 'POST', path: '/api/v1/hosts', body: {} })",
        "async () => paduraksa.request({ method: 'DELETE', path: '/api/v1/hosts/host_a', confirm: true })",
        "async () => paduraksa.request({ method: 'GET', path: '/api/v1/tokens' })",
        "async () => { for (let i = 0; i < 30; i++) { await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); } }",
        "async () => { for (;;) { } }",
        "async () => { const a = []; for (;;) a.push('x'.repeat(1e6) + a.length); }",
        "async () => { function f() { return f() + 1; } return f(); }",
        "async () => { const s = 'a'.repeat(60000); return s.replace(/a/g, \"$'\").length; }",
        "async () => 'x'.repeat('1e9').length",
        "async () => ''.concat(...new Array(90000).fill('x'.repeat(1e6))).length",
        "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.flat(Infinity).length; }",
        "async () => { var a = []; for (var i = 0; i < 200000; i++) a = [a]; return JSON.stringify(a).length; }",
        "async () => { var o = {}; for (var i = 0; i < 30; i++) o = { a: o, b: o }; return JSON.stringify(o).length; }",
        "async () => { var f = function () { return 1; }; for (var i = 0; i < 200000; i++) f = f.bind(null); return f(); }",
        "async () => 1n << 1000000000n",
        "async () => /(a+)+$/.test('a'.repeat(60) + 'b')",
        "async () => { const f = () => Promise.resolve().then(f); f(); return 1; }",
        "async () => { await new Promise(() => { }); }",
        "async () => { throw { a: 1 }; }",
        "async () => { let x = ; }",
        "async () => 'é€😀'.repeat(50000)",
        "async () => Array.from({ length: 5000 }, (_, i) => 'row ' + i)",
    ];

    private sealed class RecordingDispatcher : IApiDispatcher
    {
        public List<ApiCall> Calls { get; } = [];

        public ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return ValueTask.FromResult(new ApiReply(200, "application/json", "{\"ok\":true}", "\"3\""));
        }
    }
}
