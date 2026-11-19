using System.Text;

using Paduraksa.Tests.Acme.Dns;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// A <a href="https://github.com/letsencrypt/pebble">Pebble</a> ACME test CA that <b>really validates DNS-01</b>: it is started in Docker without
/// <c>PEBBLE_VA_ALWAYS_VALID</c>, with a <c>pebble-challtestsrv</c> container as its resolver, so a challenge passes only if the TXT
/// record is in that server when Pebble asks (<see cref="SetTxtAsync"/>, <see cref="SetCnameAsync"/> write there through its management API).
/// The propagation check asks <see cref="Dns"/>, a fake authoritative server (challtestsrv doesn't set the AA bit the check requires) that the
/// tests keep in step. Skips like <see cref="PebbleFixture"/> without Docker.
/// </summary>
/// <remarks>
/// Both containers run on the host's network (<c>--network host</c>), each listener on a probed loopback port, so the host's firewall
/// is not between them and neither is a bridge: a bridge or a network of their own adds interfaces to the host, and Chromium's network
/// change notifier answers that by failing the requests in flight in the browser tests, which run in another test module at the same
/// time. What this gives up is isolation: the containers share the host's loopback with everything else. That is fine here because every
/// port is probed per start, the listeners are bound to 127.0.0.1 only, and challtestsrv's other listeners (HTTP-01, HTTPS-01, TLS-ALPN-01,
/// DoH) are off, so nothing it serves is reachable from outside or collides with a fixed port. A port that was taken between the probe and the
/// bind makes a container exit at once, and the whole start is retried with fresh ports (<see cref="DockerCli.StartOnFreePortsAsync{T}"/>).
/// Docker Desktop has no host network that reaches the host's loopback (unless it is enabled in its settings): the management API does not answer
/// and the tests skip (fail under <c>PADURAKSA_REQUIRE=pebble</c>); run them on Linux or set <c>PADURAKSA_PEBBLE=off</c>.
/// </remarks>
public sealed class PebbleDnsFixture : IAsyncLifetime
{
    private const string ChallengeServerImage = DockerCli.ChallengeTestServerImage;

    private readonly List<string> _containers = [];
    private string? _configDirectory;
    private HttpClient? _management;

    /// <summary>The directory URL of the running Pebble; null when it is unavailable.</summary>
    public Uri? Directory { get; private set; }

    /// <summary>Why Pebble is unavailable; null when it is running.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The fake authoritative DNS server the propagation check asks.</summary>
    internal FakeDnsServer Dns { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PADURAKSA_PEBBLE"), "off", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "PADURAKSA_PEBBLE=off";
            return;
        }

        try
        {
            Dns = new FakeDnsServer();
            await DockerCli.RemoveLeftoversAsync(DockerCli.PebbleOwnerLabel);
            SkipReason = await DockerCli.StartOnFreePortsAsync(StartAsync);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FormatException or System.Net.Sockets.SocketException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    /// <summary>One start on freshly probed ports; null when Pebble is serving, else why it is not.</summary>
    /// <exception cref="DockerCli.PortsTakenException">Another process took a port between the probe and a container's bind.</exception>
    private async Task<string?> StartAsync()
    {
        var ports = DockerCli.FreeLoopbackPorts(6);
        int dnsPort = ports[0], managementPort = ports[1];
        var suffix = $"{Environment.ProcessId}-{Environment.TickCount64}-{dnsPort}";
        var started = new List<string>();
        try
        {
            // Off: the HTTP-01, HTTPS-01, TLS-ALPN-01 and DoH listeners, whose default ports are fixed ones on the host's network.
            var server = $"paduraksa-challtestsrv-{suffix}";
            var (serverCode, serverOutput) = await DockerCli.RunAsync(
                "run", "-d", "--name", server, "--label", $"{DockerCli.PebbleOwnerLabel}={Environment.ProcessId}", "--network", "host",
                ChallengeServerImage, "-dnsserver", $"127.0.0.1:{dnsPort}", "-management", $"127.0.0.1:{managementPort}",
                "-http01", string.Empty, "-https01", string.Empty, "-tlsalpn01", string.Empty, "-doh", string.Empty);
            if (serverCode != 0)
            {
                return $"Docker could not start the challenge test server: {serverOutput.Trim()}";
            }

            started.Add(server);
            if (await WaitUntilListeningAsync(server, dnsPort, managementPort) is { } serverProblem)
            {
                return $"The challenge test server did not become ready in time: {serverProblem}";
            }

            _management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{managementPort}/") };

            if (_configDirectory is not null)
            {
                // The config of an attempt that lost a port.
                System.IO.Directory.Delete(_configDirectory, recursive: true);
            }

            _configDirectory = System.IO.Directory.CreateTempSubdirectory("paduraksa-pebble-config-").FullName;
            var config = Path.Combine(_configDirectory, "pebble-config.json");
            await File.WriteAllTextAsync(config, PebbleFixture.ConfigFor(ports[2], ports[3], ports[4], ports[5]), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // No --rm: a container that exits on a lost port stays until its logs are read; DisposeAsync and the failure path remove it.
            var pebble = $"paduraksa-pebble-dns-{suffix}";
            var (pebbleCode, pebbleOutput) = await DockerCli.RunAsync(
                "run", "-d", "--name", pebble, "--label", $"{DockerCli.PebbleOwnerLabel}={Environment.ProcessId}", "--network", "host",
                "-e", "PEBBLE_VA_NOSLEEP=1", "-e", "PEBBLE_WFE_NONCEREJECT=0", "-e", "PEBBLE_AUTHZREUSE=0",
                "-v", $"{config}:/paduraksa/pebble-config.json:ro",
                DockerCli.PebbleImage, "-config", "/paduraksa/pebble-config.json", "-dnsserver", $"127.0.0.1:{dnsPort}");
            if (pebbleCode != 0)
            {
                return $"Docker could not start Pebble: {pebbleOutput.Trim()}";
            }

            started.Add(pebble);
            Directory = PebbleFixture.DirectoryUrl(ports[2]);
            return await PebbleFixture.WaitUntilServingAsync(Directory, pebble) is { } notReady ? $"Pebble did not become ready in time: {notReady}" : null;
        }
        catch (DockerCli.PortsTakenException)
        {
            foreach (var container in started)
            {
                _ = await DockerCli.RunAsync("rm", "-f", container);
            }

            started.Clear();
            _management?.Dispose();
            _management = null;
            throw;
        }
        finally
        {
            _containers.AddRange(started);
        }
    }

    /// <summary>
    /// Waits until the challenge test server accepts connections on both its ports (it starts its listeners one after the other); null
    /// when it does, else the last thing that went wrong.
    /// </summary>
    private static async Task<string?> WaitUntilListeningAsync(string container, params int[] ports)
    {
        var last = "no answer";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await DockerCli.ExitedAsync(container) is { } exited)
            {
                return exited;
            }

            // Unlike Pebble, it keeps running when one listener failed to bind, and the port then answers: from whoever holds it.
            await DockerCli.ThrowIfPortsTakenAsync(container);

            try
            {
                foreach (var port in ports)
                {
                    using System.Net.Sockets.TcpClient probe = new();
                    await probe.ConnectAsync(System.Net.IPAddress.Loopback, port);
                }

                await DockerCli.ThrowIfPortsTakenAsync(container);
                return null;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                last = ex.Message;
            }

            // Poll step of the wait for the challenge test server's listeners, bounded by the attempts above.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return last;
    }

    /// <summary>Sets the TXT value the CA's resolver answers for <paramref name="name"/> (replacing what was there); an empty value clears it.</summary>
    public async Task SetTxtAsync(string name, string? value)
    {
        using var response = await _management!.PostAsync(
            value is null ? "clear-txt" : "set-txt",
            new StringContent(value is null ? $"{{\"host\":\"{name}.\"}}" : $"{{\"host\":\"{name}.\",\"value\":\"{value}\"}}"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Makes the CA's resolver answer a CNAME for <paramref name="name"/>.</summary>
    public async Task SetCnameAsync(string name, string target)
    {
        using var response = await _management!.PostAsync("set-cname", new StringContent($"{{\"host\":\"{name}.\",\"target\":\"{target}.\"}}"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            _ = await DockerCli.RunAsync("rm", "-f", container);
        }

        if (_configDirectory is not null)
        {
            System.IO.Directory.Delete(_configDirectory, recursive: true);
        }

        _management?.Dispose();
        Dns?.Dispose();
    }
}
