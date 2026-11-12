using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Paduraksa.Mcp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>
/// A child that is not what it should be (PLAN §11.2): frames that are not answers to the run in progress, a flood on stderr, a caller who gives up while the
/// child starts, a child that burns CPU in the pool, and what a real child inherits. A shell script plays the child where a frame has to be forged.
/// </summary>
public sealed class SandboxHostileChildTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("a result for code that never ran, with the wrong run id", "WRONG")]
    [InlineData("a result with no run id at all", "")]
    public async Task AResultTheChildHadReadyBeforeTheCodeArrivedIsNotTheAnswer(string name, string runId)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        var frame = runId.Length == 0
            ? FakeChild.Frame("""{"t":"result","ok":true,"value":"\"FABRICATED\"","truncated":false}""")
            : FakeChild.Result(runId, "\\\"FABRICATED\\\"");
        await using var tools = FakeChild.Tools($"{frame}; sleep 5");
        var outcome = await tools.SearchAsync("async () => 'the real code'", Ct);
        Assert.Equal("The script was stopped: it broke the protocol.", outcome.Text);
        Assert.DoesNotContain("FABRICATED", outcome.Text, StringComparison.Ordinal);
        Assert.Equal(0, tools.Pool.ChildCount);
        Assert.NotNull(name);
    }

    [Fact]
    public async Task ARequestQueuedBeforeTheCodeWasSentIsNotServed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        ScriptedDispatcher dispatcher = new();
        var queued = FakeChild.Frame("""{"t":"request","rid":"GUESS","id":1,"options":"{\"method\":\"GET\",\"path\":\"/api/v1/hosts\"}"}""");
        await using var tools = FakeChild.Tools($"{queued}; sleep 5");
        var outcome = await tools.ExecuteAsync("async () => 1", dispatcher, Ct);
        Assert.StartsWith("The script was stopped: ", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task AFrameFromAnEarlierRunIsABreachOfTheNextOne()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        var runs = 0;
        List<string> log = [];
        await using var tools = SandboxFixture.Create(log: log.Add, command: FakeChild.After($"sleep 0.3; {FakeChild.Result("RUNONE")}; sleep 5"));
        tools.Pool.RunIds = () => ++runs == 1 ? "RUNONE" : "RUNTWO";
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);

        // The same fake answers the second run with the first run's id.
        var second = await tools.SearchAsync("async () => 2", Ct);
        Assert.StartsWith("The script was stopped: ", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrameASleepingChildSendsWhileItIsInThePoolKillsItAtOnceAndItIsNeverHandedOut()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        List<string> log = [];
        await using var tools = FakeChild.Tools($"sleep 0.3; {FakeChild.Result(FakeChild.FixedRunId)}; sleep 1; {FakeChild.Result(FakeChild.FixedRunId, "STALE")}; sleep 10", log: log.Add);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        Assert.Equal(1, tools.Pool.ChildCount);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);

        for (var i = 0; i < 60 && Directory.Exists($"/proc/{pid}"); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"), "the child was not killed when it spoke while idle");
        Assert.Contains(log, l => l.Contains("while it was idle and was killed", StringComparison.Ordinal));

        // The next call does not meet it.
        var next = await tools.SearchAsync("async () => 2", Ct);
        Assert.DoesNotContain("STALE", next.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChildThatFloodsStderrIsKilledAfterAFewHundredKilobytesAndCostsTheParentLittle()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        List<string> log = [];
        await using var tools = FakeChild.Tools("yes 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' >&2", log: log.Add);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var started = Stopwatch.StartNew();
        var outcome = await tools.SearchAsync("async () => 1", Ct);
        Assert.Equal("The script was stopped: it wrote too much to stderr.", outcome.Text);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), started.Elapsed.ToString());
        Assert.True(Process.GetCurrentProcess().TotalProcessorTime - cpu < TimeSpan.FromSeconds(3), "reading the flood cost the parent a lot of CPU");
        Assert.Contains(log, l => l.Contains("stderr", StringComparison.Ordinal));
        Assert.Equal(0, tools.Pool.ChildCount);
    }

    [Fact]
    public async Task ACallerWhoGivesUpWhileTheChildStartsDoesNotLeaveItRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        await using var tools = SandboxFixture.Create(command: FakeChild.Raw("exec sleep 31.7"));
        using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.SearchAsync("async () => 1", giveUp.Token));
        Assert.Equal(0, tools.Pool.ChildCount);

        for (var i = 0; i < 40 && SleepersOf("31.7").Count > 0; i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Empty(SleepersOf("31.7"));
    }

    [Fact]
    public async Task AWarmChildThatSpinsWhileItIsIdleIsKilledByTheSweep()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell stands in for the child");
        ManualTimeProvider time = new();
        List<string> log = [];
        await using var tools = FakeChild.Tools($"sleep 0.3; {FakeChild.Result(FakeChild.FixedRunId)}; while :; do :; done", idle: TimeSpan.FromMinutes(10), time: time, log: log.Add);
        Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
        var pid = Assert.Single(tools.Pool.ChildProcessIds);

        // The sweep looks at CPU used since the last look: real time has to pass for a spinner to have used some.
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(1500, Ct);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, tools.Pool.ChildCount);
        Assert.Contains(log, l => l.Contains("used CPU while it was idle", StringComparison.Ordinal));
        for (var i = 0; i < 40 && Directory.Exists($"/proc/{pid}"); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"));
    }

    [Fact]
    public async Task ARealChildInheritsNoneOfTheServersDescriptors()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var file = Path.GetTempFileName();
        try
        {
            using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, Ct);
            using var accepted = await listener.AcceptTcpClientAsync(Ct);

            await using var tools = SandboxFixture.Create();
            Assert.Equal("1", (await tools.SearchAsync("async () => 1", Ct)).Text);
            var pid = Assert.Single(tools.Pool.ChildProcessIds);

            var mine = Links(Environment.ProcessId).Where(l => l.StartsWith("socket:", StringComparison.Ordinal)).ToHashSet();
            Assert.NotEmpty(mine);
            var theirs = Links(pid);
            Assert.DoesNotContain(theirs, l => l.Contains(file, StringComparison.Ordinal));
            Assert.DoesNotContain(theirs, mine.Contains);
            Assert.All(theirs, link => Assert.Matches(@"^(pipe:|socket:|anon_inode:|/dev/null$|/dev/urandom$|/dev/random$|/memfd:|/proc/|/usr/|/lib|/opt/|/home/|/tmp/\.|/sys/|/etc/|/var/)", link));
            Assert.DoesNotContain(theirs, l => l.StartsWith("/tmp/", StringComparison.Ordinal) && l.Contains(Path.GetFileName(file), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void LandlockDeniesFileAccessToTheThreadThatAppliedIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Landlock is Linux");
        // Per thread, so the rest of the test process is untouched.
        string? status = null;
        Exception? read = null;
        var readable = false;
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "x");
            Thread thread = new(() =>
            {
                // Everything the denied read needs is loaded first: after the restriction a lazy load of an assembly is itself a denied read.
                _ = File.ReadAllText(file);
                try
                {
                    File.ReadAllText("/nonexistent-preload");
                }
                catch (IOException)
                {
                }

                Assert.True(ProcessHardening.SetNoNewPrivs());
                status = ProcessHardening.RestrictFileSystem(allThreads: false);
                if (!status.Contains("no file system access", StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    readable = File.ReadAllText(file) == "x";
                }
                catch (UnauthorizedAccessException ex)
                {
                    read = ex;
                }
            });
            thread.Start();
            thread.Join();
            Assert.SkipWhen(status is null || !status.Contains("no file system access", StringComparison.Ordinal), "this kernel has no Landlock: " + status);
            Assert.False(readable, "the restricted thread could still read a file");
            Assert.IsType<UnauthorizedAccessException>(read);
            Assert.Equal("x", File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ACgroupRefusalIsNamedByItsErrorAndTheProbeNeverThrows()
    {
        Assert.Contains("EACCES", ChildCgroup.Describe("writing x", new UnauthorizedAccessException()), StringComparison.Ordinal);
        Assert.Contains("EROFS", ChildCgroup.Describe("writing x", new IOException("Read-only file system : '/sys/fs/cgroup/x'")), StringComparison.Ordinal);
        Assert.Contains("EBUSY", ChildCgroup.Describe("writing x", new IOException("Device or resource busy : '/sys/fs/cgroup/x'")), StringComparison.Ordinal);
        Assert.Contains("IOException", ChildCgroup.Describe("writing x", new IOException("something else")), StringComparison.Ordinal);

        var (cgroup, status) = ChildCgroup.Probe();
        Assert.False(string.IsNullOrWhiteSpace(status));
        Assert.True(cgroup is not null || !status.StartsWith("cgroup v2 at ", StringComparison.Ordinal), status);
    }

    private static List<string> Links(int pid) =>
    [
        .. Directory.GetFiles($"/proc/{pid}/fd")
            .Select(fd => new FileInfo(fd).LinkTarget)
            .Where(t => t is not null)
            .Select(t => t!),
    ];

    private static List<string> SleepersOf(string argument) =>
    [
        .. Directory.GetDirectories("/proc")
            .Where(d => int.TryParse(Path.GetFileName(d), out _))
            .Select(d =>
            {
                try
                {
                    return File.ReadAllText(Path.Combine(d, "cmdline"));
                }
                catch (IOException)
                {
                    return string.Empty;
                }
            })
            .Where(c => c.Contains("sleep\0" + argument, StringComparison.Ordinal)),
    ];
}
