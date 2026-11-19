using System.Diagnostics;
using Npgsql;
using Paduraksa.Tests.Shared;

namespace Paduraksa.TypedIds.Tests;

/// <summary>
/// A throwaway PostgreSQL in Docker for the round-trip tests. Tests <b>skip</b> when <see cref="SkipReason"/> is set
/// (no Docker, no way to get the image, or <c>PADURAKSA_POSTGRES=off</c>), and fail instead when <c>PADURAKSA_REQUIRE</c> names <c>postgres</c>.
/// </summary>
/// <remarks>
/// The container runs on the host's network (<c>--network host</c>) and listens on a probed loopback port. A bridge container adds a
/// veth interface to the host, and Chromium's network change notifier answers that by failing the requests in flight in the browser
/// tests, which run in another test module at the same time. Docker Desktop has no host network that reaches the host's loopback (unless
/// it is enabled in its settings): the connection then fails after a minute and the tests fail; run them on Linux or set <c>PADURAKSA_POSTGRES=off</c>.
/// </remarks>
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
            await DockerCli.RemoveLeftoversAsync(OwnerLabel);
            SkipReason = await DockerCli.StartOnFreePortsAsync(() => StartAsync(ct));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    /// <summary>One start on a freshly probed port; null when PostgreSQL is serving, else why it is not.</summary>
    /// <exception cref="DockerCli.PortsTakenException">Another process took the port between the probe and PostgreSQL's bind.</exception>
    private async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        var port = DockerCli.FreeLoopbackPorts(1)[0];
        var name = $"paduraksa-postgres-{Environment.ProcessId}-{Environment.TickCount64}-{port}";
        // No --rm: a container that exits on a lost port stays until its logs are read; DisposeAsync and the failure path remove it.
        var (code, output) = await DockerCli.RunAsync(
            "run", "-d", "--name", name, "--label", $"{OwnerLabel}={Environment.ProcessId}", "--network", "host",
            "-e", "POSTGRES_PASSWORD=test", Image, "-c", "listen_addresses=127.0.0.1", "-c", $"port={port}");
        if (code != 0)
        {
            return $"Docker could not start {Image}: {output.Trim()}";
        }

        _container = name;
        ConnectionString = $"Host=127.0.0.1;Port={port};Username=postgres;Password=test;Database=postgres;Pooling=false;Timeout=2";
        try
        {
            return await WaitUntilServingAsync(name, cancellationToken);
        }
        catch
        {
            _ = await DockerCli.RunAsync("rm", "-f", name);
            _container = null;
            throw;
        }
    }

    /// <summary>
    /// The server is not up when the container starts: the image's init script runs a first server that does not listen
    /// on TCP, stops it and starts the real one, so a connection that works is the only reliable signal. A container that has
    /// exited will never answer, so it is checked about once a second.
    /// </summary>
    private async Task<string?> WaitUntilServingAsync(string container, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            if (attempt % 4 == 0 && await DockerCli.ExitedAsync(container) is { } exited)
            {
                return $"PostgreSQL did not start: {exited}";
            }

            try
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync(cancellationToken);
                return null;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(1))
            {
                // Poll step of the wait for PostgreSQL's listener, bounded by the deadline in the filter.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
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
