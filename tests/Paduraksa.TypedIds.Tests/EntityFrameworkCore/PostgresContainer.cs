using System.Diagnostics;
using Npgsql;
using Paduraksa.Tests.Shared;

namespace Paduraksa.TypedIds.Tests;

/// <summary>
/// A throwaway PostgreSQL in Docker for the round-trip tests. Tests <b>skip</b> when <see cref="SkipReason"/> is set
/// (no Docker, no way to get the image, or <c>PADURAKSA_POSTGRES=off</c>), and fail instead when <c>PADURAKSA_REQUIRE</c> names <c>postgres</c>.
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    private const string Image = "postgres:17-alpine@sha256:526f86d93c6ea08fd3f1fe5605650cacc781b2c6013169a0ad8f42c7d3d84f32";

    /// <summary>The label on the container; its value is the id of the test process that started it.</summary>
    private const string OwnerLabel = "paduraksa.test.postgres";

    private string? _container;

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PADURAKSA_POSTGRES"), "off", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "PADURAKSA_POSTGRES=off";
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        try
        {
            await RemoveLeftoversAsync();
            var name = $"paduraksa-postgres-{Environment.ProcessId}-{Environment.TickCount64}";
            var (code, output) = await DockerCli.RunAsync(
                "run", "-d", "--name", name, "--label", $"{OwnerLabel}={Environment.ProcessId}", "-p", "127.0.0.1::5432",
                "-e", "POSTGRES_PASSWORD=test", Image);
            if (code != 0)
            {
                SkipReason = $"Docker could not start {Image}: {output.Trim()}";
                return;
            }

            _container = name;
            (code, output) = await DockerCli.RunAsync("port", name, "5432/tcp");
            if (code != 0)
            {
                throw new InvalidOperationException($"docker port failed: {output.Trim()}");
            }

            var port = int.Parse(output.Split('\n')[0].Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            ConnectionString = $"Host=127.0.0.1;Port={port};Username=postgres;Password=test;Database=postgres;Pooling=false;Timeout=2";
            await WaitUntilServingAsync(ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    /// <summary>
    /// The server is not up when the container starts: the image's init script runs a first server that does not listen
    /// on TCP, stops it and starts the real one, so a connection that works is the only reliable signal.
    /// </summary>
    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(1))
            {
                // Poll step of the wait for PostgreSQL's listener, bounded by the deadline in the filter.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    /// <summary>Removes containers a test run that was killed left behind: labelled with the id of a process that is gone.</summary>
    private static async Task RemoveLeftoversAsync()
    {
        var (code, listing) = await DockerCli.RunAsync("ps", "-a", "--filter", $"label={OwnerLabel}", "--format", $"{{{{.ID}}}} {{{{.Label \"{OwnerLabel}\"}}}}");
        if (code != 0)
        {
            return;
        }

        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var owner) && !IsRunning(owner))
            {
                _ = await DockerCli.RunAsync("rm", "-f", parts[0]);
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

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            _ = await DockerCli.RunAsync("rm", "-f", _container);
        }
    }
}
