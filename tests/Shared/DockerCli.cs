using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Paduraksa.Tests.Shared;

/// <summary>Runs the <c>docker</c> command line for the tests that start a container (Pebble, ...), shared by the test projects that do.</summary>
internal static class DockerCli
{
    /// <summary>
    /// The Pebble ACME test server (v2.10.1), pinned by digest so a run does not change with what <c>latest</c> is today. Docker
    /// uses the local copy when it has this digest and pulls it otherwise.
    /// </summary>
    public const string PebbleImage = "ghcr.io/letsencrypt/pebble:v2.10.1@sha256:ddf230642b1a584f519f32e347de1b05a6e4c1f6c35c1863b33effeab5f78199";

    /// <summary>
    /// The Pebble challenge test server (a DNS server whose records a test sets over HTTP), pinned by digest like <see cref="PebbleImage"/>: the
    /// DNS-01 Pebble tests depend on how it answers.
    /// </summary>
    public const string ChallengeTestServerImage = "ghcr.io/letsencrypt/pebble-challtestsrv@sha256:0152c8cb506e2ecba2900ed47d23932e27e3e6575a7d2c8ef93c7719ee3dea4c";

    /// <summary>The label on every Pebble container a test run starts; its value is the id of the test process that started it.</summary>
    public const string PebbleOwnerLabel = "paduraksa.test.pebble";

    /// <summary>How many times a start is tried when a probed port turned out to be taken.</summary>
    private const int PortAttempts = 5;

    /// <summary>
    /// <paramref name="count"/> distinct TCP ports that are free on IPv4 loopback. The probes are all held open until the last one is
    /// taken (one at a time, the OS may hand back the number it just released), and released again before the caller starts a container:
    /// nothing reserves them, so a start that loses the race has to be retried (<see cref="StartOnFreePortsAsync{T}"/>).
    /// </summary>
    public static int[] FreeLoopbackPorts(int count)
    {
        var probes = new List<TcpListener>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                TcpListener probe = new(IPAddress.Loopback, 0);
                probe.Start();
                probes.Add(probe);
            }

            return [.. probes.Select(p => ((IPEndPoint)p.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var probe in probes)
            {
                probe.Stop();
            }
        }
    }

    /// <summary>Removes containers a test run that was killed left behind: labelled <paramref name="label"/> with the id of a process that is gone.</summary>
    public static async Task RemoveLeftoversAsync(string label)
    {
        var (code, listing) = await RunAsync("ps", "-a", "--filter", $"label={label}", "--format", $"{{{{.ID}}}} {{{{.Label \"{label}\"}}}}");
        if (code != 0)
        {
            return;
        }

        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var owner) && !IsRunning(owner))
            {
                _ = await RunAsync("rm", "-f", parts[0]);
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A container on the host's network exited because a port it was given was taken between the probe and its bind.</summary>
    public sealed class PortsTakenException(string diagnostics) : Exception(diagnostics);

    /// <summary>
    /// Runs <paramref name="start"/> (which probes ports, starts a container and waits for it), again with fresh ports when it throws
    /// <see cref="PortsTakenException"/>, up to five times. Containers use <c>--network host</c> and so can't be given a port by Docker
    /// as <c>-p 127.0.0.1::port</c> does: another process on the host or a leftover container can take a probed port in the gap.
    /// </summary>
    public static async Task<T> StartOnFreePortsAsync<T>(Func<Task<T>> start)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await start();
            }
            catch (PortsTakenException) when (attempt < PortAttempts)
            {
                // Another process won a port: probe again.
            }
        }
    }

    /// <summary>
    /// Null while <paramref name="container"/> is running (or Docker is briefly unable to say); otherwise why it will never answer, with its logs.
    /// Throws <see cref="PortsTakenException"/> when the logs say a port was in use. The caller removes the container.
    /// </summary>
    public static async Task<string?> ExitedAsync(string container)
    {
        var (code, state) = await RunAsync("inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", container);
        var status = state.Trim();
        if (code != 0 || !(status.StartsWith("exited", StringComparison.Ordinal) || status.StartsWith("dead", StringComparison.Ordinal)))
        {
            return null;
        }

        var (_, logs) = await RunAsync("logs", "--tail", "40", container);
        var diagnostics = $"the container {status}.{Environment.NewLine}docker logs:{Environment.NewLine}{logs.Trim()}";
        if (SaysAddressInUse(logs))
        {
            throw new PortsTakenException(diagnostics);
        }

        return diagnostics;
    }

    /// <summary>
    /// Throws <see cref="PortsTakenException"/> when the logs of <paramref name="container"/> say a bind failed. For a server that stays up
    /// after one of its listeners failed to bind (pebble-challtestsrv does), where <see cref="ExitedAsync"/> never sees an exit.
    /// </summary>
    public static async Task ThrowIfPortsTakenAsync(string container)
    {
        var (_, logs) = await RunAsync("logs", "--tail", "40", container);
        if (SaysAddressInUse(logs))
        {
            throw new PortsTakenException($"docker logs:{Environment.NewLine}{logs.Trim()}");
        }
    }

    /// <summary>The wording differs by server: Go says "address already in use", PostgreSQL "Address in use".</summary>
    private static bool SaysAddressInUse(string logs) =>
        logs.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || logs.Contains("address in use", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs <c>docker</c> with <paramref name="arguments"/> (at most three minutes: a first run may pull an image). The output is standard output followed by standard error (the other way round on failure, so the error leads).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception"><c>docker</c> is not installed.</exception>
    public static Task<(int ExitCode, string Output)> RunAsync(params string[] arguments) =>
        RunAsync(TimeSpan.FromMinutes(3), arguments);

    /// <summary>Runs <c>docker</c> with <paramref name="arguments"/> and a caller-chosen timeout (an image build needs more than three minutes).</summary>
    /// <inheritdoc cref="RunAsync(string[])"/>
    public static async Task<(int ExitCode, string Output)> RunAsync(TimeSpan timeout, params string[] arguments)
    {
        ProcessStartInfo start = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("docker did not start.");
        using CancellationTokenSource limit = new(timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, process.ExitCode == 0 ? await output + await error : (await error) + await output);
    }
}
