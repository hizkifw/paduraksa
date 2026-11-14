using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// The server under test: the real <c>paduraksa</c> assembly of a Release build, started as <c>dotnet paduraksa.dll</c> with its settings in environment variables,
/// as a container would give them, pinned to the server's CPUs with <c>taskset</c> and carrying the startup hook that reports its GC and thread-pool figures.
/// It stays the same data directory across a restart, which is what a restart is.
/// </summary>
internal sealed partial class ServerProcess(SoakOptions options, string dataDirectory, string logPath)
{
    private readonly object _logGate = new();
    private Process? _process;

    public int Pid => _process?.Id ?? 0;

    public string DataDirectory { get; } = dataDirectory;

    public string ProbeFile => Path.Combine(options.OutDirectory, "probe.log");

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int Kill(int pid, int signal);

    public void Start()
    {
        var useTaskset = File.Exists("/usr/bin/taskset") || File.Exists("/bin/taskset");
        ProcessStartInfo start = new()
        {
            FileName = useTaskset ? "taskset" : "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (useTaskset)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(options.ServerCpus);
            start.ArgumentList.Add("dotnet");
        }

        start.ArgumentList.Add(options.ServerDll);
        var env = start.Environment;
        env["Paduraksa__DataDirectory"] = DataDirectory;
        env["Paduraksa__Listen__Http"] = options.PublicHttp.ToString();
        env["Paduraksa__Listen__Https"] = options.PublicHttps.ToString();
        env["Paduraksa__Listen__PublicHttpsPort"] = options.PublicHttps.ToString();
        env["Paduraksa__Listen__Admin"] = options.Admin.ToString();
        env["Paduraksa__Listen__AdminBind"] = "127.0.0.1";
        env["Paduraksa__Listen__Http3"] = options.Http3 ? "true" : "false";
        env["Paduraksa__Development__PlaceholderCertificate"] = "true";
        env["Paduraksa__Mcp__Enabled"] = options.Mcp ? "true" : "false";
        env["Paduraksa__ShutdownTimeout"] = "00:00:30";
        env["DOTNET_STARTUP_HOOKS"] = options.ProbeDll;
        env["PADURAKSA_SOAK_PROBE_FILE"] = ProbeFile;
        env["PADURAKSA_SOAK_PROBE_SECONDS"] = "5";
        _process = Process.Start(start) ?? throw new InvalidOperationException("The server did not start.");
        Pump(_process.StandardOutput);
        Pump(_process.StandardError);
    }

    /// <summary>SIGTERM, then the exit: the graceful stop a container's <c>docker stop</c> asks for.</summary>
    public async Task<(int ExitCode, TimeSpan Took)> StopAsync()
    {
        var process = _process ?? throw new InvalidOperationException("Not running.");
        var watch = Stopwatch.StartNew();
        _ = Kill(process.Id, 15);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return (-9, watch.Elapsed);
        }

        return (process.ExitCode, watch.Elapsed);
    }

    public void KillHard()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public bool HasExited => _process is null || _process.HasExited;

    private void Pump(StreamReader reader) => _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (_logGate)
            {
                File.AppendAllText(logPath, line + "\n");
            }
        }
    });
}
