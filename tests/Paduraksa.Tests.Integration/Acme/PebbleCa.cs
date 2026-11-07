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
/// <para>Per test, so each has a CA of its own with no orders left over: the container starts in about a second, runs with <c>--rm</c> and a
/// label naming the test process (<see cref="PebbleRoot"/> removes leftovers of a killed run), and is removed on dispose.</para>
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

    /// <summary>Starts Pebble so that its validation of <paramref name="names"/> goes to the host's port <paramref name="validationPort"/>.</summary>
    public static async Task<PebbleCa> StartAsync(int validationPort, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        int directoryPort = FreePort();
        string directory = System.IO.Directory.CreateTempSubdirectory("paduraksa-pebble-config-").FullName;
        string config = Path.Combine(directory, "pebble-config.json");
        await File.WriteAllTextAsync(config, ConfigFor(validationPort, directoryPort, FreePort()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        string container = $"paduraksa-pebble-{Environment.ProcessId}-{Environment.TickCount64}-{validationPort}";
        List<string> arguments =
        [
            "run", "-d", "--rm", "--name", container, "--label", $"{PebbleRoot.OwnerLabel}={Environment.ProcessId}", "--network", "host",
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

    private static string ConfigFor(int validationPort, int directoryPort, int managementPort) => $$"""
        {
          "pebble": {
            "listenAddress": "127.0.0.1:{{directoryPort}}",
            "managementListenAddress": "127.0.0.1:{{managementPort}}",
            "certificate": "test/certs/localhost/cert.pem",
            "privateKey": "test/certs/localhost/key.pem",
            "httpPort": {{validationPort}},
            "tlsPort": 5001,
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

    /// <summary>Waits for the directory to answer; the handler takes any certificate, since this only asks whether Pebble is up.</summary>
    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
#pragma warning disable CA5359 // Readiness probe of a throwaway container on a loopback port; the code under test verifies the real chain.
        using HttpClient http = new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true } });
#pragma warning restore CA5359
        // Pebble refuses a request without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Paduraksa-Tests/1.0");
        string last = "no answer";
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(Directory, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                last = ex.GetBaseException().Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"Pebble did not become ready: {last}");
    }
}
