using System.Text;

using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// A <a href="https://github.com/letsencrypt/pebble">Pebble</a> ACME test CA for the tests that talk to a real CA, started in
/// a Docker container with validation switched off (<c>PEBBLE_VA_ALWAYS_VALID</c>: it issues without fetching the challenge, so
/// these tests prove the protocol exchange with a real CA and not the gate or DNS). The container is removed on dispose. Tests <b>skip</b>
/// when it is unavailable (<see cref="SkipReason"/>): no Docker, no way to pull the image, or <c>PADURAKSA_PEBBLE=off</c>. To use a Pebble that is already running (started with always-valid validation), set
/// <c>PADURAKSA_PEBBLE_DIRECTORY</c> to its directory URL.
/// </summary>
/// <remarks>
/// The container runs on the host's network (<c>--network host</c>) with every listener on a probed loopback port, written into a config
/// of its own. A bridge container adds a veth interface to the host, and Chromium's network change notifier answers that by failing
/// the requests in flight in the browser tests, which run in another test module at the same time. A probed port can be taken before
/// Pebble binds it; Pebble then exits at once and the start is retried with fresh ports (<see cref="DockerCli.StartOnFreePortsAsync{T}"/>).
/// Only Linux's host network reaches the host's loopback, so elsewhere the tests skip at once (fail under <c>PADURAKSA_REQUIRE=pebble</c>)
/// unless <c>PADURAKSA_PEBBLE_DIRECTORY</c> names a running Pebble.
/// </remarks>
public sealed class PebbleFixture : IAsyncLifetime
{
    private const string Image = DockerCli.PebbleImage;

    private string? _container;
    private string? _configDirectory;

    /// <summary>The directory URL of the running Pebble; null when it is unavailable.</summary>
    public Uri? Directory { get; private set; }

    /// <summary>Why Pebble is unavailable; null when it is running.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The transport for talking to Pebble: its certificate comes from a test CA, so it is accepted without a chain check. Never follows redirects, as the ACME client requires.</summary>
#pragma warning disable CA5359 // Pebble's listener certificate comes from its own throwaway test CA, on a loopback port this fixture started.
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
    };
#pragma warning restore CA5359

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PADURAKSA_PEBBLE"), "off", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "PADURAKSA_PEBBLE=off";
            return;
        }

        if (Environment.GetEnvironmentVariable("PADURAKSA_PEBBLE_DIRECTORY") is { Length: > 0 } existing)
        {
            Directory = new Uri(existing);
            SkipReason = await WaitUntilServingAsync(Directory) is { } problem ? $"Pebble at {existing} did not answer: {problem}" : null;
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            // Host networking reaches the host's loopback only on Linux; elsewhere the fixture would wait out its timeout for a sure skip.
            SkipReason = "The Pebble tests need Docker on Linux (host networking).";
            return;
        }

        try
        {
            await DockerCli.RemoveLeftoversAsync(DockerCli.PebbleOwnerLabel);
            SkipReason = await DockerCli.StartOnFreePortsAsync(StartAsync);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FormatException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    /// <summary>One start on freshly probed ports; null when Pebble is serving, else why it is not.</summary>
    /// <exception cref="DockerCli.PortsTakenException">Another process took a port between the probe and Pebble's bind.</exception>
    private async Task<string?> StartAsync()
    {
        var ports = DockerCli.FreeLoopbackPorts(4);
        if (_configDirectory is not null)
        {
            // The config of an attempt that lost a port.
            System.IO.Directory.Delete(_configDirectory, recursive: true);
        }

        _configDirectory = System.IO.Directory.CreateTempSubdirectory("paduraksa-pebble-config-").FullName;
        var config = Path.Combine(_configDirectory, "pebble-config.json");
        await File.WriteAllTextAsync(config, ConfigFor(ports[0], ports[1], ports[2], ports[3]), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // No random bad nonces and no authorization reuse, so a run is deterministic. No --rm: a Pebble that exits on a lost port
        // stays until its logs are read; DisposeAsync and the failure path remove it.
        var name = $"paduraksa-pebble-{Environment.ProcessId}-{Environment.TickCount64}-{ports[0]}";
        var (code, output) = await DockerCli.RunAsync(
            "run", "-d", "--name", name, "--label", $"{DockerCli.PebbleOwnerLabel}={Environment.ProcessId}", "--network", "host",
            "-e", "PEBBLE_VA_ALWAYS_VALID=1", "-e", "PEBBLE_VA_NOSLEEP=1", "-e", "PEBBLE_WFE_NONCEREJECT=0", "-e", "PEBBLE_AUTHZREUSE=0",
            "-v", $"{config}:/paduraksa/pebble-config.json:ro",
            Image, "-config", "/paduraksa/pebble-config.json");
        if (code != 0)
        {
            return $"Docker could not start Pebble: {output.Trim()}";
        }

        _container = name;
        Directory = DirectoryUrl(ports[0]);
        try
        {
            return await WaitUntilServingAsync(Directory, name) is { } notReady ? $"Pebble did not become ready in time: {notReady}" : null;
        }
        catch (DockerCli.PortsTakenException)
        {
            _ = await DockerCli.RunAsync("rm", "-f", name);
            _container = null;
            throw;
        }
    }

    internal static Uri DirectoryUrl(int port) => new($"https://127.0.0.1:{port}/dir", UriKind.Absolute);

    /// <summary>
    /// Pebble's config with every listener on loopback and a probed port. The values are the image's own
    /// (<c>/test/config/pebble-config.json</c>) except the listen addresses and ports. Pebble only connects out to <c>httpPort</c> and <c>tlsPort</c> when it
    /// validates HTTP-01 or TLS-ALPN-01, which these tests never ask for; they are probed so the config never names a port another process owns.
    /// </summary>
    internal static string ConfigFor(int directoryPort, int managementPort, int httpPort, int tlsPort) => $$"""
        {
          "pebble": {
            "listenAddress": "127.0.0.1:{{directoryPort}}",
            "managementListenAddress": "127.0.0.1:{{managementPort}}",
            "certificate": "test/certs/localhost/cert.pem",
            "privateKey": "test/certs/localhost/key.pem",
            "httpPort": {{httpPort}},
            "tlsPort": {{tlsPort}},
            "ocspResponderURL": "",
            "externalAccountBindingRequired": false,
            "domainBlocklist": ["blocked-domain.example"],
            "retryAfter": { "authz": 3, "order": 5 },
            "keyAlgorithm": "ecdsa",
            "profiles": {
              "default": { "description": "The profile you know and love", "validityPeriod": 7776000 },
              "shortlived": { "description": "A short-lived cert profile, without actual enforcement", "validityPeriod": 518400 }
            }
          }
        }
        """;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            _ = await DockerCli.RunAsync("rm", "-f", _container);
        }

        if (_configDirectory is not null)
        {
            System.IO.Directory.Delete(_configDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Waits for the directory to answer; null when it does, else the last thing that went wrong. When <paramref name="container"/> is given, one that
    /// has already exited is reported at once instead of being polled for 30 seconds.
    /// </summary>
    /// <exception cref="DockerCli.PortsTakenException">The container exited because a port was taken.</exception>
    internal static async Task<string?> WaitUntilServingAsync(Uri directory, string? container = null)
    {
        using HttpClient http = new(CreateHandler());
        // Pebble refuses a request without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Paduraksa-Tests/1.0");
        var last = "no answer";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (container is not null && attempt % 2 == 0 && await DockerCli.ExitedAsync(container) is { } exited)
            {
                return exited;
            }

            try
            {
                using var response = await http.GetAsync(directory);
                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                last = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                // Not up yet.
                last = ex.GetBaseException().Message;
            }

            // Poll step of the wait for Pebble to answer, bounded by the deadline above.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return last;
    }
}
