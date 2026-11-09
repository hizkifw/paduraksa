using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Acme;

/// <summary>
/// What the Pebble tests share: whether Docker and the image are there, and Pebble's own CA certificate, copied out of the image once so
/// the process under test can trust it (<c>Paduraksa:Acme:TrustedRootsFile</c>). Tests <b>skip</b> when <see cref="SkipReason"/> is set:
/// no Docker, a platform without Docker's host network (only Linux is supported), no way to get the image, or <c>PADURAKSA_PEBBLE=off</c>.
/// </summary>
public sealed class PebbleRoot : IAsyncLifetime
{
    internal const string Image = DockerCli.PebbleImage;

    /// <summary>The label on every container these tests start; its value is the id of the test process that started it.</summary>
    internal const string OwnerLabel = "paduraksa.test.pebble";

    private string? _directory;

    /// <summary>Why Pebble is unavailable; null when it can be started.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>A PEM file with the CA certificate that signed Pebble's listener certificate (valid for 127.0.0.1).</summary>
    public string RootFile => Path.Combine(_directory!, "pebble.minica.pem");

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PADURAKSA_PEBBLE"), "off", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "PADURAKSA_PEBBLE=off";
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            // The validation authority has to reach the process under test on the host's loopback, which only Linux's host network gives.
            SkipReason = "The Pebble tests need Docker on Linux (host networking).";
            return;
        }

        _directory = Directory.CreateTempSubdirectory("paduraksa-pebble-").FullName;
        string container = $"paduraksa-pebble-root-{Environment.ProcessId}-{Environment.TickCount64}";
        try
        {
            await RemoveLeftoversAsync();

            // `create` pulls the image when it is not there; nothing runs.
            (int code, string output) = await DockerCli.RunAsync("create", "--name", container, Image);
            if (code != 0)
            {
                SkipReason = $"Docker could not get the Pebble image: {output.Trim()}";
                return;
            }

            (code, output) = await DockerCli.RunAsync("cp", $"{container}:/test/certs/pebble.minica.pem", RootFile);
            if (code != 0)
            {
                SkipReason = $"Pebble's CA certificate could not be copied out of the image: {output.Trim()}";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
        finally
        {
            try
            {
                _ = await DockerCli.RunAsync("rm", "-f", container);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                // Docker was not there to begin with; nothing was created.
            }
        }
    }

    /// <summary>Removes Pebble containers a test run that was killed left behind: labelled with the id of a process that is gone.</summary>
    private static async Task RemoveLeftoversAsync()
    {
        (int code, string listing) = await DockerCli.RunAsync("ps", "-a", "--filter", $"label={OwnerLabel}", "--format", $"{{{{.ID}}}} {{{{.Label \"{OwnerLabel}\"}}}}");
        if (code != 0)
        {
            return;
        }

        foreach (string line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int owner) && !IsRunning(owner))
            {
                _ = await DockerCli.RunAsync("rm", "-f", parts[0]);
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_directory is not null)
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A <a href="https://github.com/letsencrypt/pebble">Pebble</a> ACME test CA that <b>validates for real</b>: no
/// <c>PEBBLE_VA_ALWAYS_VALID</c>, so an HTTP-01 challenge is fetched from the process under test.
/// </summary>
/// <remarks>
/// <para><b>Topology.</b> Pebble runs in a Docker container on the <b>host's network</b> (<c>--network host</c>), listening on loopback ports this
/// class picks (the directory and the management interface). Its validation authority resolves the certificate names through the
/// container's <c>/etc/hosts</c> (<c>--add-host name:127.0.0.1</c>) and fetches
/// <c>http://name:&lt;validationPort&gt;/.well-known/acme-challenge/token</c> there, which is the <b>public HTTP listener of the process under test</b>
/// (it listens on every address, loopback included). The port comes from a Pebble config this class writes: <c>httpPort</c> is Pebble's
/// validation port, and the polling hints are one second so the tests stay short. No DNS server is needed. Host networking is what
/// works on Linux with a firewall (the bridge's gateway address is usually not reachable from a container); it is not available on
/// Docker Desktop, where these tests fail to validate (run them on Linux, or set <c>PADURAKSA_PEBBLE=off</c>).</para>
/// <para>Per test, so each has a CA of its own with no orders left over: the container starts in about a second, carries a label naming
/// the test process (<see cref="PebbleRoot"/> removes leftovers of a killed run), and is removed on dispose. It is not started with
/// <c>--rm</c>: when it exits on its own, the readiness wait can read its exit code and logs before the failure path removes it.</para>
/// </remarks>
internal sealed class PebbleCa : IAsyncDisposable
{
    private readonly string _container;
    private readonly string _directory;

    private PebbleCa(string container, string directory, Uri directoryUrl)
    {
        _container = container;
        _directory = directory;
        Directory = directoryUrl;
    }

    /// <summary>The ACME directory URL (<c>https://127.0.0.1:port/dir</c>), served with a certificate <see cref="PebbleRoot.RootFile"/> vouches for.</summary>
    public Uri Directory { get; }

    /// <summary>A free TCP port, for the process under test's public HTTP listener, which Pebble has to be told about before it starts.</summary>
    public static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>A probed free port that is none of <paramref name="taken"/> (probes release their port, so the OS may hand
    /// the same one back).</summary>
    private static int FreePortExcept(params int[] taken)
    {
        while (true)
        {
            int port = FreePort();
            if (Array.IndexOf(taken, port) < 0)
            {
                return port;
            }
        }
    }

    /// <summary>
    /// Starts Pebble so that its validation of <paramref name="names"/> goes to the host's port <paramref name="validationPort"/>.
    /// </summary>
    /// <remarks>
    /// Pebble's three own listeners are on the host's network, and each port is probed before Docker binds it: another test's host
    /// (or a container that outlived its test) can take one in the gap, and Pebble then exits at once with "address already in use".
    /// So the whole container start is retried with fresh ports, as <see cref="TestPorts.StartOnFreePortAsync"/> retries a host, and
    /// a container that has already exited is reported at once instead of being polled until the readiness deadline.
    /// </remarks>
    public static async Task<PebbleCa> StartAsync(int validationPort, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await StartOnFreshPortsAsync(validationPort, names, cancellationToken);
            }
            catch (PebblePortsTakenException) when (attempt < PortAttempts)
            {
                // Another process won the ports: probe again.
            }
        }
    }

    private static async Task<PebbleCa> StartOnFreshPortsAsync(int validationPort, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        // Its three own listeners need three different ports. Probing each one separately can hand back the same number
        // twice (the probes release it again), and Pebble then exits at once because two listeners can't share it. Every
        // port is picked per instance (and per attempt): the fixed TLS port 5001 every container used to share is the same
        // problem with anything else on the host (or a container that outlived its test).
        int directoryPort = FreePort();
        int managementPort = FreePortExcept(directoryPort);
        int tlsPort = FreePortExcept(directoryPort, managementPort);
        string directory = System.IO.Directory.CreateTempSubdirectory("paduraksa-pebble-config-").FullName;
        string config = Path.Combine(directory, "pebble-config.json");
        await File.WriteAllTextAsync(
            config, ConfigFor(validationPort, directoryPort, managementPort, tlsPort), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        string container = $"paduraksa-pebble-{Environment.ProcessId}-{Environment.TickCount64}-{validationPort}";
        List<string> arguments =
        [
            // No --rm: when Pebble exits on its own (a lost port race) the container is still there for the readiness
            // wait to read its exit code and logs; DisposeAsync (and the failure path below) removes it either way.
            "run", "-d", "--name", container, "--label", $"{PebbleRoot.OwnerLabel}={Environment.ProcessId}", "--network", "host",
            "-e", "PEBBLE_VA_ALWAYS_VALID=0", "-e", "PEBBLE_VA_NOSLEEP=1", "-e", "PEBBLE_WFE_NONCEREJECT=0", "-e", "PEBBLE_AUTHZREUSE=0",
            "-v", $"{config}:/paduraksa/pebble-config.json:ro",
        ];
        foreach (string name in names)
        {
            arguments.AddRange(["--add-host", $"{name}:127.0.0.1"]);
        }

        arguments.AddRange([PebbleRoot.Image, "-config", "/paduraksa/pebble-config.json"]);
        (int code, string output) = await DockerCli.RunAsync([.. arguments]);
        if (code != 0)
        {
            System.IO.Directory.Delete(directory, recursive: true);
            throw new InvalidOperationException($"Docker could not start Pebble: {output.Trim()}");
        }

        try
        {
            Uri url = new($"https://127.0.0.1:{directoryPort}/dir", UriKind.Absolute);
            PebbleCa ca = new(container, directory, url);
            await ca.WaitUntilServingAsync(cancellationToken);
            return ca;
        }
        catch
        {
            _ = await DockerCli.RunAsync("rm", "-f", container);
            System.IO.Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await DockerCli.RunAsync("rm", "-f", _container);
        System.IO.Directory.Delete(_directory, recursive: true);
    }

    /// <summary>How many times a start is tried when a probed port turned out to be taken.</summary>
    private const int PortAttempts = 5;

    /// <summary>Pebble exited at once because one of its probed ports was taken between the probe and Docker's bind.</summary>
    private sealed class PebblePortsTakenException(string diagnostics) : Exception(diagnostics);

    private static string ConfigFor(int validationPort, int directoryPort, int managementPort, int tlsPort) => $$"""
        {
          "pebble": {
            "listenAddress": "127.0.0.1:{{directoryPort}}",
            "managementListenAddress": "127.0.0.1:{{managementPort}}",
            "certificate": "test/certs/localhost/cert.pem",
            "privateKey": "test/certs/localhost/key.pem",
            "httpPort": {{validationPort}},
            "tlsPort": {{tlsPort}},
            "ocspResponderURL": "",
            "externalAccountBindingRequired": false,
            "domainBlocklist": ["blocked-domain.example"],
            "retryAfter": { "authz": 1, "order": 1 },
            "keyAlgorithm": "ecdsa",
            "profiles": {
              "default": { "description": "The profile you know and love", "validityPeriod": 7776000 }
            }
          }
        }
        """;

    /// <summary>
    /// Waits for the directory to answer; the handler takes any certificate, since this only asks whether Pebble is up.
    /// This polls an external process (a Docker container that is still starting), not a state the process under test can
    /// signal, so the upper bound is deliberately generous (two minutes) and the failure carries Docker's own diagnostics.
    /// </summary>
    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
#pragma warning disable CA5359 // Readiness probe of a throwaway container on a loopback port; the code under test verifies the real chain.
        using HttpClient http = new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true } })
        {
            Timeout = TimeSpan.FromSeconds(5), // one probe cannot hang for HttpClient's default 100 s
        };
#pragma warning restore CA5359
        // Pebble refuses a request without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Paduraksa-Tests/1.0");
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        long started = Stopwatch.GetTimestamp();
        string last = "no answer";
        int attempt = 0;
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            // A container that already exited (a lost port race, a bad config) will never answer: say so at once instead
            // of polling it for the whole timeout. Cheap enough to check about once a second.
            if (attempt++ % 4 == 0)
            {
                (int inspectCode, string state) = await DockerCli.RunAsync("inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", _container);
                string status = state.Trim();
                if (inspectCode != 0)
                {
                    // Docker itself may be briefly busy under load; the next probes will tell whether the container is gone.
                    last = $"docker inspect failed: {status}";
                }
                else if (status.StartsWith("exited", StringComparison.Ordinal) || status.StartsWith("dead", StringComparison.Ordinal))
                {
                    // It will never answer: say why (exit code and logs below) without polling out the whole budget.
                    last = $"the container {status}";
                    break;
                }
            }

            try
            {
                using HttpResponseMessage response = await http.GetAsync(Directory, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                last = "the readiness request timed out";
            }
            catch (HttpRequestException ex)
            {
                last = ex.GetBaseException().Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        (_, string logs) = await DockerCli.RunAsync("logs", "--tail", "40", _container);
        string diagnostics = $"{last}.{Environment.NewLine}docker logs:{Environment.NewLine}{logs.Trim()}";
        if (logs.Contains("address already in use", StringComparison.Ordinal))
        {
            // One of the probed ports was taken between the probe and the bind: the caller retries the whole start with fresh ones.
            throw new PebblePortsTakenException(diagnostics);
        }

        throw new TimeoutException($"Pebble did not become ready within {timeout.TotalSeconds:F0} s: {diagnostics}");
    }
}
