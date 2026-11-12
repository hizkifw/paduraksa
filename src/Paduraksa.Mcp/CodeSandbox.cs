using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace Paduraksa.Mcp;

/// <summary>
/// Runs one model-written function in a fresh Jint engine on a thread of its own (PLAN §11.2). Nothing is shared between calls: the engine, its
/// globals and its budget die with the call. What a script can reach is the engine's standard library minus <see cref="SandboxGuards"/>, plus the one
/// global its tool installs; no CLR object is ever handed to it.
/// </summary>
internal static class CodeSandbox
{
    /// <summary>How long past <see cref="SandboxLimits.TotalTime"/> the caller waits for a thread stuck in one native call before it stops waiting.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A regular expression is compiled when the code is parsed, with the parser's timeout; the engine's own does not reach it.</summary>
    private static readonly ScriptPreparationOptions PreparationOptions = new()
    {
        ParsingOptions = new ScriptParsingOptions { RegexTimeout = RegexTimeout },
        FoldConstants = false,
    };

    private static readonly Lock Gate = new();
    private static int s_running;

    public static async Task<SandboxOutcome> RunAsync(string code, SandboxHost host, SandboxLimits limits, Action<Exception>? unexpected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(limits);
        if (code.Length > limits.MaxCodeChars)
        {
            return SandboxOutcome.Error($"The code is {code.Length:N0} characters; the limit is {limits.MaxCodeChars:N0}. Send less code.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return SandboxOutcome.Error("The code is empty. Send an async arrow function, e.g. async () => { ... }.");
        }

        if (!TryEnter(limits.MaxConcurrentRuns))
        {
            return SandboxOutcome.Error("Other scripts are running; try again in a moment.");
        }

        TaskCompletionSource<SandboxOutcome> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource total = new(limits.TotalTime);
        Thread thread = new(
            () =>
            {
                try
                {
                    done.TrySetResult(Run(code, host, limits, unexpected, total.Token, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unexpected?.Invoke(ex);
                    done.TrySetResult(SandboxOutcome.Error("The sandbox failed on this script. Try a simpler one.", host.Requests));
                }
                catch (OperationCanceledException)
                {
                    done.TrySetCanceled(cancellationToken);
                }
                finally
                {
                    Leave();
                }
            },
            limits.StackBytes)
        {
            IsBackground = true,
            Name = "paduraksa-mcp-sandbox",
        };
        try
        {
            thread.Start();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            Leave();
            unexpected?.Invoke(ex);
            return SandboxOutcome.Error("The sandbox could not start a thread. Try again later.");
        }

        var finished = await Task.WhenAny(done.Task, Task.Delay(limits.TotalTime + Grace, cancellationToken)).ConfigureAwait(false);
        if (finished != done.Task)
        {
            // The thread stays in its native call and releases its place when that returns; the caller is not kept waiting for it.
            cancellationToken.ThrowIfCancellationRequested();
            return SandboxOutcome.Error("The script did not stop in time and was abandoned. Do less per call.", host.Requests);
        }

        return await done.Task.ConfigureAwait(false);
    }

    private static bool TryEnter(int max)
    {
        lock (Gate)
        {
            if (s_running >= max)
            {
                return false;
            }

            s_running++;
            return true;
        }
    }

    private static void Leave()
    {
        lock (Gate)
        {
            s_running--;
        }
    }

    private static SandboxOutcome Run(string code, SandboxHost host, SandboxLimits limits, Action<Exception>? unexpected, CancellationToken total, CancellationToken caller)
    {
        SandboxBudget budget = new(limits, total, caller);
        try
        {
            using var engine = new Engine(options =>
            {
                options.Strict();
                options.DisableStringCompilation();
                options.LimitRecursion(limits.MaxCallDepth);
                options.MaxArraySize(limits.MaxArraySize);
                // Off by default in 4.17: without it, native recursion over data a script built (a chain of bound functions) can reach the end of the stack,
                // which ends the process. On, it is a RangeError the script can catch.
                options.Constraints.StackOverflowGuard = true;
                options.MaxJsonParseDepth(64);
                options.RegexTimeoutInterval(RegexTimeout);
                options.Constraint(budget);
            });
            SandboxGuards.Install(engine, limits, budget);
            host.Install(engine, limits, budget);
            var runner = engine.Evaluate(Runner);

            SettledOutcome settled = new();
            var report = new ClrFunction(engine, "report", (_, args) =>
            {
                settled.Succeeded = args[0].AsBoolean();
                settled.Value = args[1];
                settled.Done = true;
                return JsValue.Undefined;
            });

            budget.Arm();
            var script = Engine.PrepareScript(code, "code.js", strict: true, PreparationOptions);
            if (UsesBigInt(script.Program!))
            {
                return SandboxOutcome.Error("BigInt is not available (1n, 123n): its arithmetic cannot be bounded. Use numbers or strings.", host.Requests);
            }

            var function = engine.Evaluate(script);
            if (!function.IsCallable())
            {
                return SandboxOutcome.Error(
                    "The code must evaluate to a function: an async arrow function, e.g. async () => { ... }. Its last expression is what runs.",
                    host.Requests);
            }

            engine.Call(runner, JsValue.Undefined, [function, report]);
            while (true)
            {
                engine.Advanced.ProcessTasks();
                if (settled.Done)
                {
                    break;
                }

                if (!host.ServeNext())
                {
                    return SandboxOutcome.Error(
                        "The function never finished: it waits on a promise that nothing will settle. Await only paduraksa.request or promises you create and resolve.",
                        host.Requests);
                }
            }

            if (!settled.Succeeded)
            {
                return SandboxOutcome.Error(ScriptErrors.Describe(settled.Value), host.Requests);
            }

            // Requests the function left unawaited are the model's intent: complete them, as the result's note says.
            host.FunctionReturned();
            while (host.ServeNext())
            {
                engine.Advanced.ProcessTasks();
            }

            return Serialize(settled.Value, limits, host);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return SandboxOutcome.Error(ScriptErrors.Describe(ex, limits), host.Requests);
        }
        catch (Exception ex)
        {
            unexpected?.Invoke(ex);
            return SandboxOutcome.Error(ScriptErrors.Describe(ex, limits), host.Requests);
        }
    }

    /// <summary>A BigInt shift or power can run for minutes inside one native call, which no limit interrupts, so the literals are refused before anything runs (and the global is removed).</summary>
    private static bool UsesBigInt(Acornima.Ast.Node program)
    {
        Stack<Acornima.Ast.Node> pending = new();
        pending.Push(program);
        while (pending.TryPop(out var node))
        {
            if (node is Acornima.Ast.BigIntLiteral)
            {
                return true;
            }

            foreach (var child in node.ChildNodes)
            {
                if (child is not null)
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    private static SandboxOutcome Serialize(JsValue value, SandboxLimits limits, SandboxHost host)
    {
        CappedJson json = new(limits.MaxOutputBytes);
        try
        {
            if (value.IsUndefined())
            {
                return new SandboxOutcome(false, "null", host.Note, host.Requests);
            }

            json.Write(value);
            return new SandboxOutcome(false, json.Text, host.Note, host.Requests);
        }
        catch (JsonBudgetExceededException)
        {
            var note = $"The result is longer than {limits.MaxOutputBytes / 1024} KiB and was cut there. Return less: filter, map to the fields you need, or slice.";
            return new SandboxOutcome(false, json.Prefix(), host.Note is null ? note : note + " " + host.Note, host.Requests);
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is SandboxLimitException or Jint.Runtime.JavaScriptException or Jint.Runtime.PromiseRejectedException or Jint.Runtime.StatementsCountOverflowException
            or Jint.Runtime.MemoryLimitExceededException or Jint.Runtime.RecursionDepthOverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException
            or Jint.ScriptPreparationException;

    /// <summary>
    /// Runs the function and reports how it ended. The three natives it relies on are captured before the script's code is evaluated, so a script that
    /// replaces <c>Promise.prototype.then</c> cannot hide its own ending.
    /// </summary>
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
}
