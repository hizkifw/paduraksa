using System.Diagnostics;
using System.Text;

using Paduraksa.Mcp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>
/// The proof of the design (docs/design/admin-surfaces.md "MCP in code mode (M10)"): a script that misbehaves costs only its child. The scripts that were once dangerous in-process run on a stock engine
/// here, with the engine's own constraints on (a clean error) and off (the boundary alone), and the parent notices, kills the child, answers with a fixed
/// message and serves the next call on a new one.
/// </summary>
public sealed class SandboxBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SandboxLimits Boundary(Func<SandboxLimits, SandboxLimits>? change = null)
    {
        var limits = new SandboxLimits { EngineConstraints = false, ScriptTime = TimeSpan.FromSeconds(3), CpuTime = TimeSpan.FromSeconds(3), ChildRssBytes = 300L * 1024 * 1024 };
        return change?.Invoke(limits) ?? limits;
    }

    public static TheoryData<string> Dangerous =>
    [
        "async () => { const s = 'a'.repeat(60000); return s.replace(/a/g, \"$'\").length; }",
        "async () => { const s = 'a'.repeat(60000); return s.replace(/a/g, '$`').length; }",
        "async () => 'x'.repeat('1e9').length",
        "async () => String.prototype.repeat.call(new String('x'), 2e9).length",
        "async () => 'abc'.padStart(2 ** 31).length",
        "async () => ''.concat(...new Array(90000).fill('x'.repeat(1e6))).length",
        "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return a.flat(Infinity).length; }",
        "async () => { var a = []; for (var i = 0; i < 90000; i++) a = [a]; return String(a).length; }",
        "async () => { var a = []; for (var i = 0; i < 200000; i++) a = [a]; return JSON.stringify(a).length; }",
        "async () => { var o = {}; for (var i = 0; i < 30; i++) o = { a: o, b: o }; return JSON.stringify(o).length; }",
        "async () => { var f = function () { return 1; }; for (var i = 0; i < 200000; i++) f = f.bind(null); return f(); }",
        "async () => { var f = function () { return 1; }; for (var i = 0; i < 200000; i++) f = f.bind(null); return new f(); }",
        "async () => 1n << 1000000000n",
        "async () => (3n ** 100000000n).toString().length",
        "async () => /(a+)+$/.test('a'.repeat(60) + 'b')",
        "async () => { for (;;) { } }",
        "async () => { const a = []; for (;;) a.push('x'.repeat(1e6) + a.length); }",
        "async () => new Array(1e9).fill('x'.repeat(100)).length",
        "async () => { function f() { return f() + 1; } return f(); }",
        "async () => { const f = () => Promise.resolve().then(f); f(); return 1; }",
    ];

    [Theory]
    [MemberData(nameof(Dangerous))]
    public async Task ADangerousScriptOnAStockEngineWithItsConstraintsOnEndsAsAResultOrACleanErrorAndTheNextCallWorks(string code)
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { ScriptTime = TimeSpan.FromSeconds(4), CpuTime = TimeSpan.FromSeconds(4) });
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync(code, Ct);
        Assert.True(!outcome.IsError || outcome.Text.Length < 400, outcome.Text);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), started.Elapsed.ToString());
        Assert.DoesNotContain("   at ", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
    }

    /// <summary>The ones that cannot finish: the parent has to stop them.</summary>
    public static TheoryData<string> Runaway =>
    [
        "async () => { for (;;) { } }",
        "async () => { const a = []; for (;;) a.push('x'.repeat(1e6) + a.length); }",
        "async () => /(a+)+$/.test('a'.repeat(60) + 'b')",
        "async () => { function f() { return f() + 1; } return f(); }",
        "async () => { const f = () => Promise.resolve().then(f); f(); return 1; }",
    ];

    [Theory]
    [MemberData(nameof(Runaway))]
    public async Task AScriptThatCannotFinishIsStoppedByTheParentWhenTheEngineHasNoConstraints(string code)
    {
        await using var tools = SandboxFixture.Create(Boundary());
        var outcome = await tools.SearchAsync(code, Ct);
        Assert.True(outcome.IsError, outcome.Text);
        Assert.True(outcome.Text.StartsWith("The script was stopped: ", StringComparison.Ordinal) || outcome.Text.Contains("memory", StringComparison.Ordinal), outcome.Text);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
    }

    [Theory]
    [MemberData(nameof(Dangerous))]
    public async Task TheSameScriptsWithNoEngineConstraintsAtAllCostOnlyTheirChild(string code)
    {
        await using var tools = SandboxFixture.Create(Boundary());
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync(code, Ct);
        // The script finishes (stock Jint copes with much of this), or is stopped by the parent, or ends in the engine's own error (a string length, memory): either way the child
        // is all it can cost, and what the caller reads is short and holds no stack.
        Assert.True(outcome.Text.Length < 400 || !outcome.IsError, outcome.Text);
        Assert.DoesNotContain("   at ", outcome.Text, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), started.Elapsed.ToString());
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
    }

    [Theory]
    [InlineData("async () => /(a+)+$/.test('a'.repeat(60) + 'b')")]
    [InlineData("async () => new RegExp('(a|aa)+$').test('a'.repeat(80) + 'b')")]
    [InlineData("async () => /(a|aa)+$/.test('a'.repeat(80) + 'b')")]
    [InlineData("async () => new RegExp('(a+)+$').test('a'.repeat(60) + 'b')")]
    [InlineData("async () => ('a'.repeat(60) + 'b').replace(/(a+)+$/, '')")]
    [InlineData("async () => ('x'.repeat(40) + '!').match(/^(x+x+)+y/)")]
    [InlineData("async () => { try { /(a+)+$/.test('a'.repeat(60) + 'b'); } catch (e) { return 'caught ' + e.name; } }")]
    public async Task CatastrophicBacktrackingFailsInsideTheRunWithinTheRegexTimeoutAndTheChildIsKept(string code)
    {
        await using var tools = SandboxFixture.Create();
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync(code, Ct);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), "the CPU watchdog, not the regex timeout, ended it: " + started.Elapsed);
        Assert.DoesNotContain("The script was stopped", outcome.Text, StringComparison.Ordinal);
        Assert.True(outcome.Text.Contains("regular expression took too long", StringComparison.Ordinal) || outcome.Text.StartsWith("\"caught ", StringComparison.Ordinal), outcome.Text);
        Assert.Equal("2", (await tools.SearchAsync("async () => 2", Ct)).Text);
        Assert.Equal(pid, Assert.Single(tools.Pool.ChildProcessIds));
    }

    [Fact]
    public async Task AStackOverflowInTheChildKillsOnlyTheChild()
    {
        await using var tools = SandboxFixture.Create(Boundary());
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        var before = Assert.Single(tools.Pool.ChildProcessIds);
        var outcome = await tools.SearchAsync("async () => { function f(n) { return f(n + 1) + 1; } return f(0); }", Ct);
        Assert.StartsWith("The script was stopped: ", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("2", (await tools.SearchAsync("async () => 2", Ct)).Text);
        Assert.NotEqual(before, Assert.Single(tools.Pool.ChildProcessIds));
        Assert.False(Directory.Exists($"/proc/{before}"));
    }

    [Fact]
    public async Task AMemoryBreachKills()
    {
        await using var tools = SandboxFixture.Create(Boundary(l => l with { ChildRssBytes = 150L * 1024 * 1024 }));
        var outcome = await tools.SearchAsync("async () => { const a = []; for (;;) a.push('x'.repeat(1e6) + a.length); }", Ct);
        // The parent's sample, or the child's own heap limit if it came first: a script that allocates is stopped either way.
        Assert.True(outcome.IsError);
        Assert.Contains("memory", outcome.Text, StringComparison.Ordinal);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
    }

    [Fact]
    public async Task AWallTimeBreachKills()
    {
        await using var tools = SandboxFixture.Create(Boundary(l => l with { ScriptTime = TimeSpan.FromSeconds(1), CpuTime = TimeSpan.FromMinutes(5) }));
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync("async () => { for (;;) { } }", Ct);
        Assert.Equal("The script was stopped: it ran for more than 1 s.", outcome.Text);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), started.Elapsed.ToString());
    }

    [Fact]
    public async Task ACpuBreachKills()
    {
        await using var tools = SandboxFixture.Create(Boundary(l => l with { CpuTime = TimeSpan.FromSeconds(1), ScriptTime = TimeSpan.FromMinutes(5) }));
        var outcome = await tools.SearchAsync("async () => { for (;;) { } }", Ct);
        Assert.Equal("The script was stopped: it used more than 1 s of CPU time.", outcome.Text);
    }

    [Fact]
    public async Task TheWallClockDoesNotRunWhileTheParentAnswersARequestButTheTotalDoes()
    {
        await using var slow = SandboxFixture.Create(new SandboxLimits { ScriptTime = TimeSpan.FromSeconds(1), TotalTime = TimeSpan.FromSeconds(30) });
        var ok = await slow.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", new DelayedDispatcher(TimeSpan.FromSeconds(2)), Ct);
        Assert.Equal("200", ok.Text);

        await using var total = SandboxFixture.Create(new SandboxLimits { TotalTime = TimeSpan.FromSeconds(1) });
        var started = Stopwatch.StartNew();
        var outcome = await total.ExecuteAsync("async () => await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })", new DelayedDispatcher(TimeSpan.FromMinutes(1)), Ct);
        Assert.Equal("The script was stopped: the call took more than 1 s in all.", outcome.Text);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), started.Elapsed.ToString());
        Assert.Equal("1", (await total.SearchAsync("async () => 1", Ct)).Text);
    }

    [Fact]
    public async Task TheRequestAfterTheLimitKillsTheChildAndTheDispatcherSawOnlyTheAllowedOnes()
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { MaxRequests = 3 });
        ScriptedDispatcher dispatcher = new();
        var outcome = await tools.ExecuteAsync("async () => { for (let i = 0; i < 10; i++) { await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' }); } return 'done'; }", dispatcher, Ct);
        Assert.Equal("The script was stopped: it made more than 3 requests.", outcome.Text);
        Assert.Equal(3, dispatcher.Calls.Count);
        Assert.Equal("\"ok\"", (await tools.SearchAsync("async () => 'ok'", Ct)).Text);
    }

    [Fact]
    public async Task ACallerWhoGoesAwayKillsTheChild()
    {
        await using var tools = SandboxFixture.Create();
        using CancellationTokenSource gone = new();
        DelayedDispatcher dispatcher = new(TimeSpan.FromMinutes(1));
        var running = tools.ExecuteAsync("async () => await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })", dispatcher, gone.Token);
        await dispatcher.Called.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await WaitUntilGoneAsync(pid);
    }

    [Fact]
    public async Task OnlyAsManyScriptsRunAtOnceAsThereAreChildren()
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { MaxChildren = 2, AcquireTimeout = TimeSpan.FromMilliseconds(400) });
        GateDispatcher gate = new();
        var first = tools.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", gate, Ct);
        var second = tools.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", gate, Ct);
        await gate.WaitForAsync(2);
        var third = await tools.SearchAsync("async () => 1", Ct);
        Assert.Equal("Other scripts are running; try again in a moment.", third.Text);
        Assert.Equal(2, tools.Pool.ChildCount);
        gate.Release();
        Assert.Equal("200", (await first).Text);
        Assert.Equal("200", (await second).Text);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        Assert.Equal(2, tools.Pool.ChildCount);
    }

    [Fact]
    public async Task AChildIsReplacedAfterTheRunsItMayServe()
    {
        await using var tools = SandboxFixture.Create(new SandboxLimits { MaxRunsPerChild = 2 });
        await tools.SearchAsync("async () => 1", Ct);
        var first = Assert.Single(tools.Pool.ChildProcessIds);
        await tools.SearchAsync("async () => 1", Ct);
        Assert.Equal(0, tools.Pool.ChildCount);
        await WaitUntilGoneAsync(first);
        await tools.SearchAsync("async () => 1", Ct);
        Assert.NotEqual(first, Assert.Single(tools.Pool.ChildProcessIds));
    }

    [Fact]
    public async Task IdleChildrenAreDespawnedAndTheNextCallStartsOneLazily()
    {
        ManualTimeProvider time = new();
        List<string> log = [];
        await using var tools = SandboxFixture.Create(idle: TimeSpan.FromMinutes(1), time: time, log: log.Add);
        Assert.Equal(0, tools.Pool.ChildCount);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        var first = Assert.Single(tools.Pool.ChildProcessIds);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, tools.Pool.ChildCount);
        time.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(0, tools.Pool.ChildCount);
        await WaitUntilGoneAsync(first);
        Assert.Contains(log, l => l.Contains("despawned", StringComparison.Ordinal));

        Assert.Equal("2", (await tools.SearchAsync("async () => 2", Ct)).Text);
        Assert.NotEqual(first, Assert.Single(tools.Pool.ChildProcessIds));
    }

    [Fact]
    public async Task AChildThatIsUsedStaysWarm()
    {
        ManualTimeProvider time = new();
        await using var tools = SandboxFixture.Create(idle: TimeSpan.FromMinutes(1), time: time);
        await tools.SearchAsync("async () => 1", Ct);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(40));
            await tools.SearchAsync("async () => 1", Ct);
        }

        Assert.Equal(pid, Assert.Single(tools.Pool.ChildProcessIds));
    }

    [Fact]
    public async Task ShuttingDownKillsEveryChildAndLeavesNoOrphans()
    {
        var tools = SandboxFixture.Create(new SandboxLimits { MaxChildren = 3 });
        GateDispatcher gate = new();
        var running = Enumerable.Range(0, 2)
            .Select(_ => tools.ExecuteAsync("async () => (await paduraksa.request({ method: 'GET', path: '/api/v1/hosts' })).status", gate, Ct))
            .ToList();
        await gate.WaitForAsync(2);
        await tools.SearchAsync("async () => 1", Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var pids = tools.Pool.ChildProcessIds.ToList();
        Assert.Equal(3, pids.Count);

        await tools.DisposeAsync();
        foreach (var pid in pids)
        {
            Assert.False(Directory.Exists($"/proc/{pid}"), $"child {pid} outlived the pool");
        }

        foreach (var call in running)
        {
            var outcome = await call;
            Assert.StartsWith("The script was stopped: ", outcome.Text, StringComparison.Ordinal);
        }

        gate.Release();
    }

    private const string SentinelName = "PADURAKSA_SANDBOX_TEST_SENTINEL";

    [Fact]
    public async Task TheChildHasNoneOfTheServersEnvironmentAndAppliesWhatItCan()
    {
        // A sentinel nothing reads, not a real PADURAKSA_MASTER_KEY: the process environment is shared with the test classes running beside
        // this one, and a bad key there fails every host they start. The allowlist assertion below covers the key itself.
        Environment.SetEnvironmentVariable(SentinelName, "a-value-the-child-must-not-have");
        try
        {
            await using var tools = SandboxFixture.Create();
            await tools.SearchAsync("async () => 1", Ct);
            var pid = Assert.Single(tools.Pool.ChildProcessIds);
            var environment = (await File.ReadAllTextAsync($"/proc/{pid}/environ", Ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain(environment, e => e.Contains("a-value-the-child-must-not-have", StringComparison.Ordinal) || e.StartsWith(SentinelName, StringComparison.Ordinal));
            Assert.All(environment, e => Assert.Matches("^(DOTNET_|PATH=|LD_LIBRARY_PATH=|PADURAKSA_MCP_CPU_SECONDS=)", e));
            Assert.Contains(environment, e => e.StartsWith("DOTNET_GCHeapHardLimit=0x10000000", StringComparison.Ordinal));
            // Per thread: the script thread has it.
            var scriptThread = Directory.GetDirectories($"/proc/{pid}/task").Single(t => File.ReadAllText(Path.Combine(t, "comm")).StartsWith("paduraksa-mcp-c", StringComparison.Ordinal));
            Assert.Contains("NoNewPrivs:\t1", await File.ReadAllTextAsync(Path.Combine(scriptThread, "status"), Ct), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SentinelName, null);
        }
    }

    [Fact]
    public async Task TheChildReportsWhatItAppliedAndLeavesWhenItsStdinCloses()
    {
        ProcessStartInfo info = new(SandboxFixture.Command.FileName) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in SandboxFixture.Command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var ready = await FrameCodec.ReadAsync(process.StandardOutput.BaseStream, FrameCodec.MaxFromChild, Ct);
        using var message = System.Text.Json.JsonDocument.Parse(ready!);
        Assert.Equal("ready", message.RootElement.GetProperty("t").GetString());
        var hardening = message.RootElement.GetProperty("hardening").EnumerateArray().Select(e => e.GetString()!).ToList();
        foreach (var applied in new[] { "RLIMIT_CPU", "RLIMIT_NOFILE", "RLIMIT_CORE", "PR_SET_NO_NEW_PRIVS", "PR_SET_PDEATHSIG" })
        {
            Assert.Contains($"{applied}: applied", hardening);
        }

        Assert.Contains(hardening, h => h.StartsWith("Landlock:", StringComparison.Ordinal));

        // What each of these says depends on the kernel and the architecture, but each reports and none is silent.
        Assert.Contains(hardening, h => h.StartsWith("Landlock network:", StringComparison.Ordinal));
        Assert.Contains(hardening, h => h.StartsWith("Landlock signals:", StringComparison.Ordinal));
        Assert.Contains(hardening, h => h.StartsWith("seccomp:", StringComparison.Ordinal));

        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000), "the child did not leave when its stdin closed");
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task TheServerLogsWhatTheFirstChildAppliedOnceAndTheKernelHoldsItOnEveryThread()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the hardening is Linux");
        List<string> log = [];
        await using var tools = SandboxFixture.Create(log: log.Add);
        await tools.SearchAsync("async () => 1", Ct);
        await tools.SearchAsync("async () => 2", Ct);
        var line = Assert.Single(log, l => l.StartsWith("Sandbox child hardening applied: ", StringComparison.Ordinal));
        Assert.Contains("RLIMIT_CPU: applied", line, StringComparison.Ordinal);
        Assert.Contains("seccomp:", line, StringComparison.Ordinal);

        // Where the filter says it is on every thread, the kernel agrees for the child's main thread too, not only the one that applied it.
        Assert.SkipUnless(line.Contains("seccomp: only AF_UNIX sockets, signals only to itself and ", StringComparison.Ordinal) && line.Contains("calls denied (every thread)", StringComparison.Ordinal), "the filter is on the script thread only here: " + line);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);
        foreach (var task in Directory.GetDirectories($"/proc/{pid}/task"))
        {
            try
            {
                Assert.Contains("Seccomp:\t2", await File.ReadAllTextAsync(Path.Combine(task, "status"), Ct), StringComparison.Ordinal);
            }
            catch (IOException)
            {
                // A thread that ended since the listing.
            }
        }
    }

    [Fact]
    public async Task UnderAllocationPressureTheChildKeepsCollectingWithItsSignalsLimitedToItself()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the hardening is Linux");
        List<string> log = [];
        await using var tools = SandboxFixture.Create(log: log.Add);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        var line = Assert.Single(log, l => l.StartsWith("Sandbox child hardening applied: ", StringComparison.Ordinal));
        Assert.SkipUnless(line.Contains("signals only to itself", StringComparison.Ordinal), "no seccomp filter here: " + line);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);

        // Each run allocates and drops about 40 MB in the 256 MiB heap, so the runtime collects again and again with the filter (and, where the kernel has it, Landlock's signal scope) on every thread.
        for (var i = 0; i < 25; i++)
        {
            var outcome = await tools.SearchAsync("async () => { let n = 0; for (let i = 0; i < 400; i++) { const s = 'x'.repeat(50000) + i; n += s.length; } return n; }", Ct);
            Assert.False(outcome.IsError, outcome.Text);
        }

        Assert.Equal(pid, Assert.Single(tools.Pool.ChildProcessIds));
        Assert.DoesNotContain(log, l => l.Contains("killed", StringComparison.Ordinal) || l.Contains("ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKilledChildIsGoneAndTheAnswerComesWithinHalfASecondOfTheLimit()
    {
        await using var tools = SandboxFixture.Create(Boundary(l => l with { ScriptTime = TimeSpan.FromSeconds(1) }));
        await tools.SearchAsync("async () => 1", Ct);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync("async () => { for (;;) { } }", Ct);
        var latency = started.Elapsed - TimeSpan.FromSeconds(1);
        Assert.StartsWith("The script was stopped", outcome.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists($"/proc/{pid}"));
        Assert.True(latency < TimeSpan.FromMilliseconds(500), "from the limit to the kill and the answer: " + latency);
    }

    private static string Frame(string json) => FakeChild.Frame(json);

    public static TheoryData<string, string> BrokenChildren => new()
    {
        { "garbage", "printf 'not a frame at all'; sleep 5" },
        { "oversized", "printf '\\177\\377\\377\\377'; sleep 5" },
        { "zero length", "printf '\\000\\000\\000\\000'; sleep 5" },
        { "not json", Frame("this is not json at all, but a frame") + "; sleep 5" },
        { "no type", Frame("""{"x":1}""") + "; sleep 5" },
        { "unknown type", Frame("""{"t":"surprise"}""") + "; sleep 5" },
        { "ids out of order", Frame("""{"t":"request","rid":"X","id":9,"options":"{}"}""") + "; sleep 5" },
        { "cut inside a frame", "printf '\\000\\000\\000\\100{\"t\"'; sleep 5" },
        { "exits", "exit 3" },
        { "closes its pipe", "exec 1>&-; sleep 5" },
    };

    [Theory]
    [MemberData(nameof(BrokenChildren))]
    public async Task AChildThatBreaksTheProtocolOrLeavesIsKilledAndTheCallerGetsAFixedMessage(string name, string afterReady)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        await using var tools = SandboxFixture.Create(
            new SandboxLimits { StartupTimeout = TimeSpan.FromSeconds(5), ScriptTime = TimeSpan.FromSeconds(2) },
            command: FakeChild.After($"sleep 0.4; {afterReady}"));
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync("async () => 1", Ct);
        Assert.True(outcome.IsError, name);
        Assert.StartsWith("The script was stopped: ", outcome.Text, StringComparison.Ordinal);
        Assert.True(outcome.Text.Length < 80, outcome.Text);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), name + " " + started.Elapsed);
        Assert.Equal(0, tools.Pool.ChildCount);
    }

    [Theory]
    [InlineData("printf 'junk'; sleep 5")]
    [InlineData("exit 1")]
    [InlineData("sleep 30")]
    public async Task AChildThatNeverSaysItIsReadyIsAStartFailureWithNoChildLeft(string script)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        List<string> log = [];
        await using var tools = SandboxFixture.Create(new SandboxLimits { StartupTimeout = TimeSpan.FromMilliseconds(800) }, log: log.Add, command: new SandboxChildCommand("/bin/sh", ["-c", script]));
        var outcome = await tools.SearchAsync("async () => 1", Ct);
        Assert.Equal("The sandbox could not start.", outcome.Text);
        Assert.Equal(0, tools.Pool.ChildCount);
    }

    private static async Task WaitUntilGoneAsync(int pid)
    {
        for (var i = 0; i < 100 && Directory.Exists($"/proc/{pid}"); i++)
        {
            // Poll step of a wait bounded to five seconds for the child to be gone.
            await Task.Delay(50, Ct);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"), $"process {pid} is still there");
    }

    private sealed class DelayedDispatcher(TimeSpan delay) : IApiDispatcher
    {
        /// <summary>Completed when the script's request reaches the dispatcher: the child is running by then.</summary>
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
        {
            Called.TrySetResult();
            // A slow dispatcher: the script waits on it, which is what the tests need to interrupt.
            await Task.Delay(delay, cancellationToken);
            return new ApiReply(200, "application/json", "{}");
        }
    }

    /// <summary>Holds every request until released, and says how many are waiting.</summary>
    private sealed class GateDispatcher : IApiDispatcher
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiting;

        public async ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waiting);
            await _open.Task.WaitAsync(cancellationToken);
            return new ApiReply(200, "application/json", "{}");
        }

        public void Release() => _open.TrySetResult();

        public async Task WaitForAsync(int count)
        {
            for (var i = 0; i < 200 && Volatile.Read(ref _waiting) < count; i++)
            {
                // Poll step of a wait bounded to ten seconds.
                await Task.Delay(50, Ct);
            }

            Assert.True(Volatile.Read(ref _waiting) >= count);
        }
    }
}

/// <summary>A clock a test moves by hand; its timers fire when it is advanced past them.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(callback, state, GetUtcNow() + dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => !t.Disposed && t.Next <= target).OrderBy(t => t.Next).FirstOrDefault();
                _now = due is null ? target : due.Next;
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset next, TimeSpan period) : ITimer
    {
        public DateTimeOffset Next { get; private set; } = next;

        public bool Disposed { get; private set; }

        public void Fire()
        {
            Next = period > TimeSpan.Zero ? Next + period : DateTimeOffset.MaxValue;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period1) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
