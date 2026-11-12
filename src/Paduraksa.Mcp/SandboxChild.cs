using System.Text.Json;

using Jint;
using Jint.Native;
using Jint.Native.Error;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Paduraksa.Mcp;

/// <summary>
/// The sandbox's child process (<c>paduraksa mcp-sandbox</c>, PLAN §11.2): a Jint engine with stock behaviour that runs one script at a time, and a pipe to the
/// parent. It holds no token, no configuration, no data directory and uses no network: <c>paduraksa.request</c> sends a frame and waits for the parent's
/// answer, and the parent applies the request policy. What a script can do in here is not trusted to be little; the parent bounds the process and kills it.
/// </summary>
public static class SandboxChild
{
    /// <summary>Stack of the thread that runs the loop; the engine's own native-stack guard turns recursion into an error long before it is used up.</summary>
    private const int StackBytes = 256 * 1024 * 1024;

    private const string CpuSecondsVariable = "PADURAKSA_MCP_CPU_SECONDS";

    /// <summary>Runs the child's loop to the end of its stdin (or an exit message) and returns its exit code.</summary>
    public static int Run(Stream input, Stream output)
    {
        var exit = 0;
        Thread thread = new(() => exit = Loop(input, output), StackBytes) { Name = "paduraksa-mcp-child" };
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int Loop(Stream input, Stream output)
    {
        try
        {
            var cpu = int.TryParse(Environment.GetEnvironmentVariable(CpuSecondsVariable), out var seconds) ? seconds : 300;
            var report = ProcessHardening.Apply(cpu);

            // Everything the engine will load or JIT is touched now, because the file system is closed in a moment.
            Execute("execute", "async () => JSON.stringify({ a: [1, 'x'] }) + (await paduraksa.request({ method: 'GET', path: '/api/v1/warm' })).status + /a+/.test('aa')", SandboxLimits.Default, null, (_, _) => Warm);
            Execute("search", "async () => Object.keys(spec.paths).length", SandboxLimits.Default, SpecGraph.Parse("""{"paths":{"/x":{"get":{}}},"components":{"schemas":{"A":{}}}}"""u8), (_, _) => Warm);
            PreloadAssemblies();
            report.Add(ProcessHardening.RestrictFileSystem());
            report.Add(ProcessHardening.RestrictNetwork());
            report.Add(SyscallFilter.Restrict());

            Send(output, Messages.Build(w =>
            {
                w.WriteString("t", "ready");
                w.WriteNumber("pid", Environment.ProcessId);
                w.WriteStartArray("hardening");
                foreach (var line in report)
                {
                    w.WriteStringValue(line);
                }

                w.WriteEndArray();
            }));

            SpecGraph? spec = null;
            while (true)
            {
                var frame = FrameCodec.Read(input, FrameCodec.MaxToChild);
                if (frame is null)
                {
                    return 0;
                }

                using var message = Messages.Parse(frame);
                switch (Messages.TypeOf(message))
                {
                    case "spec":
                        spec = SpecGraph.Parse(System.Text.Encoding.UTF8.GetBytes(Messages.Text(message.RootElement, "spec", FrameCodec.MaxToChild)));
                        break;
                    case "run":
                        RunScript(message.RootElement, input, output, spec);
                        break;
                    case "exit":
                        return 0;
                    default:
                        return 2;
                }
            }
        }
        catch (SandboxProtocolException ex)
        {
            Console.Error.WriteLine("protocol: " + ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("io: " + ex.Message);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 3;
        }
    }

    /// <summary>Loads what the runtime would load lazily (the console for an error line, the exception types): after the file system is closed it cannot.</summary>
    private static void PreloadAssemblies()
    {
        Console.Error.Flush();
        HashSet<string> seen = [];
        Queue<System.Reflection.Assembly> pending = new([typeof(SandboxChild).Assembly, typeof(Console).Assembly, typeof(Engine).Assembly, typeof(JsonDocument).Assembly]);
        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name && seen.Add(name))
                {
                    try
                    {
                        pending.Enqueue(System.Reflection.Assembly.Load(reference));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                    {
                        // an optional reference
                    }
                }
            }
        }
    }

    private static readonly byte[] Warm = Messages.Build(w =>
    {
        w.WriteString("t", "response");
        w.WriteNumber("id", 1);
        w.WriteNumber("status", 200);
        w.WriteNull("body");
    });

    private static void RunScript(JsonElement message, Stream input, Stream output, SpecGraph? spec)
    {
        var runId = Messages.Text(message, "rid", RunProtocol.RunIdMaxChars);
        var tool = Messages.Text(message, "tool", 16);
        var code = Messages.Text(message, "code", FrameCodec.MaxToChild);
        var limits = ReadLimits(message);
        var result = Execute(tool, code, limits, spec, (id, options) =>
        {
            var frame = Messages.Build(w =>
            {
                w.WriteString("t", "request");
                w.WriteString("rid", runId);
                w.WriteNumber("id", id);
                w.WriteString("options", options);
            });
            if (frame.Length > FrameCodec.MaxFromChild)
            {
                return null;
            }

            Send(output, frame);
            return FrameCodec.Read(input, FrameCodec.MaxToChild) ?? throw new SandboxProtocolException("The parent closed the pipe.");
        });

        Send(output, Messages.Build(w =>
        {
            w.WriteString("t", "result");
            w.WriteString("rid", runId);
            w.WriteBoolean("ok", !result.IsError);
            w.WriteString(result.IsError ? "error" : "value", result.Text);
            w.WriteBoolean("truncated", result.Truncated);
            w.WriteBoolean("recycle", result.Recycle);
            if (result.Note is { } note)
            {
                w.WriteString("note", note);
            }
        }));
    }

    private static SandboxLimits ReadLimits(JsonElement message)
    {
        var l = message.GetProperty("limits");
        return new SandboxLimits
        {
            EngineConstraints = Messages.Flag(l, "constraints"),
            EngineMaxStatements = Messages.Number(l, "statements"),
            EngineMemoryBytes = Messages.Number(l, "memoryMiB") * 1024L * 1024,
            EngineCallDepth = Messages.Number(l, "depth"),
            MaxOutputBytes = Messages.Number(l, "output"),
        };
    }

    private static void Send(Stream output, byte[] frame) => FrameCodec.Write(output, frame, FrameCodec.MaxFromChild);

    private sealed record Outcome(bool IsError, string Text, bool Truncated, string? Note, bool Recycle = false);

    /// <summary>Runs one script on a fresh engine. <paramref name="exchange"/> sends a request's options and returns the parent's answer frame, or <c>null</c> when the request cannot be framed.</summary>
    private static Outcome Execute(string tool, string code, SandboxLimits limits, SpecGraph? spec, Func<int, string, byte[]?> exchange)
    {
        try
        {
            using var engine = new Engine(options =>
            {
                options.Strict();
                if (limits.EngineConstraints)
                {
                    options.MaxStatements(limits.EngineMaxStatements);
                    options.LimitMemory(limits.EngineMemoryBytes);
                    options.LimitRecursion(limits.EngineCallDepth);
                    options.Constraints.StackOverflowGuard = true;
                }
            });

            var stringify = engine.Evaluate("JSON.stringify");
            var parse = engine.Evaluate("JSON.parse");
            ChildRequests requests = new(engine, stringify, parse, exchange);
            if (tool == "search")
            {
                if (spec is null)
                {
                    return new Outcome(true, "The API description is not loaded.", false, null);
                }

                engine.SetValue("spec", spec.Build(engine));
            }
            else
            {
                requests.Install();
            }

            var runner = engine.Evaluate(Runner);
            SettledOutcome settled = new();
            var report = new ClrFunction(engine, "report", (_, args) =>
            {
                settled.Succeeded = args[0].AsBoolean();
                settled.Value = args[1];
                settled.Done = true;
                return JsValue.Undefined;
            });

            var script = Engine.PrepareScript(code, "code.js", strict: true);
            var function = engine.Evaluate(script);
            if (!function.IsCallable())
            {
                return new Outcome(true, "The code must evaluate to a function: an async arrow function, e.g. async () => { ... }. Its last expression is what runs.", false, null);
            }

            engine.Call(runner, JsValue.Undefined, [function, report]);
            while (true)
            {
                engine.Advanced.ProcessTasks();
                if (settled.Done)
                {
                    break;
                }

                if (!requests.ServeNext())
                {
                    return new Outcome(true, "The function never finished: it waits on a promise that nothing will settle. Await only paduraksa.request or promises you create and resolve.", false, null);
                }
            }

            if (!settled.Succeeded)
            {
                return new Outcome(true, ScriptErrors.Describe(settled.Value), false, null);
            }

            // Requests the function left unawaited are the model's intent: complete them, as the result's note says.
            requests.FunctionReturned();
            while (requests.ServeNext())
            {
                engine.Advanced.ProcessTasks();
            }

            var json = settled.Value.IsUndefined() ? JsValue.Undefined : engine.Call(stringify, JsValue.Undefined, [settled.Value]);
            var text = json.IsString() ? json.AsString() : "null";
            var cut = text.Length > limits.MaxOutputBytes;
            return new Outcome(false, cut ? text[..limits.MaxOutputBytes] : text, cut, requests.Note);
        }
        catch (SandboxProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var known = ex is JavaScriptException or PromiseRejectedException or StatementsCountOverflowException or MemoryLimitExceededException
                or RecursionDepthOverflowException or ScriptPreparationException or InsufficientExecutionStackException;
            if (!known)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            }

            // A process that ran out of memory or of stack is not trusted to serve another script.
            return new Outcome(true, ScriptErrors.Describe(ex, limits), false, null, Recycle: ex is OutOfMemoryException or InsufficientMemoryException or InsufficientExecutionStackException || !known);
        }
    }

    /// <summary>The function is run and its ending reported through captured natives, so a script that replaces <c>Promise.prototype.then</c> cannot hide its own ending.</summary>
    private const string Runner = """
        (function () {
          const apply = Reflect.apply, then = Promise.prototype.then, resolved = Promise.resolve.bind(Promise);
          return function (fn, report) {
            const started = apply(then, resolved(), [fn]);
            apply(then, started, [v => report(true, v), e => report(false, e)]);
          };
        })()
        """;

    private sealed class SettledOutcome
    {
        public bool Done { get; set; }

        public bool Succeeded { get; set; }

        public JsValue Value { get; set; } = JsValue.Undefined;
    }

    /// <summary><c>paduraksa.request</c>: queues what the script asks, and serves the queue one request at a time, in order, by asking the parent.</summary>
    private sealed class ChildRequests(Engine engine, JsValue stringify, JsValue parse, Func<int, string, byte[]?> exchange)
    {
        private readonly Queue<(int Id, string Options, ManualPromise Promise)> _queue = new();
        private int _next;
        private int _unawaited;

        public string? Note => _unawaited == 0
            ? null
            : $"{_unawaited} request(s) were still pending when the function returned and were completed; await each request you depend on.";

        public void Install()
        {
            var paduraksa = new JsObject(engine);
            paduraksa.Set("request", new ClrFunction(engine, "request", Request), true);
            engine.SetValue("paduraksa", paduraksa);
        }

        public void FunctionReturned() => _unawaited = _queue.Count;

        public bool ServeNext()
        {
            if (!_queue.TryDequeue(out var pending))
            {
                return false;
            }

            var answer = exchange(pending.Id, pending.Options);
            if (answer is null)
            {
                pending.Promise.Reject(Error("TypeError", "The request is larger than the sandbox can send."));
                return true;
            }

            using var message = Messages.Parse(answer);
            var root = message.RootElement;
            if (Messages.TypeOf(message) != "response" || Messages.Number(root, "id") != pending.Id)
            {
                throw new SandboxProtocolException("The parent sent something other than the answer.");
            }

            if (root.TryGetProperty("error", out var error))
            {
                pending.Promise.Reject(Error(Messages.Text(error, "type", 32), Messages.Text(error, "message", 2000)));
                return true;
            }

            pending.Promise.Resolve(Reply(root));
            return true;
        }

        private JsValue Request(JsValue thisObject, JsValue[] args)
        {
            var promise = engine.Advanced.RegisterPromise();
            var json = args.Length > 0 && !args[0].IsUndefined() ? engine.Call(stringify, JsValue.Undefined, [args[0]]) : JsValue.Undefined;
            _queue.Enqueue((++_next, json.IsString() ? json.AsString() : "null", promise));
            return promise.Promise;
        }

        private JsObject Reply(JsonElement root)
        {
            var result = new JsObject(engine);
            result.Set("status", Messages.Number(root, "status"), true);
            result.Set("body", Body(root), true);
            if (root.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String)
            {
                result.Set("etag", etag.GetString()!, true);
            }

            if (Messages.Flag(root, "dryRun"))
            {
                result.Set("dryRun", JsBoolean.True, true);
            }

            if (root.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
            {
                result.Set("note", note.GetString()!, true);
            }

            return result;
        }

        private JsValue Body(JsonElement root)
        {
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
            {
                return JsValue.Null;
            }

            var text = body.GetString()!;
            if (Messages.Flag(root, "json"))
            {
                try
                {
                    return engine.Call(parse, JsValue.Undefined, [text]);
                }
                catch (JavaScriptException)
                {
                    // Not JSON after all (or deeper than the parser allows): the text is still the answer.
                }
            }

            return text;
        }

        private JsValue Error(string type, string message)
        {
            var constructor = engine.Evaluate(type is "TypeError" or "RangeError" ? type : "Error").AsObject();
            return new JavaScriptException((ErrorConstructor)constructor, message).Error;
        }
    }
}
