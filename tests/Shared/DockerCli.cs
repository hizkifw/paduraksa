using System.Diagnostics;

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
