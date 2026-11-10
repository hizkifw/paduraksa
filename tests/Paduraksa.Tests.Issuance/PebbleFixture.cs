using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// A <a href="https://github.com/letsencrypt/pebble">Pebble</a> ACME test CA for the tests that talk to a real CA, started in
/// a Docker container with validation switched off (<c>PEBBLE_VA_ALWAYS_VALID</c>: it issues without fetching the challenge, so
/// these tests prove the protocol exchange with a real CA and not the gate or DNS). The container uses random host ports and
/// is removed on dispose. Tests <b>skip</b> when it is unavailable (<see cref="SkipReason"/>): no Docker, no way to pull the image, or
/// <c>PADURAKSA_PEBBLE=off</c>. To use a Pebble that is already running (started with always-valid validation), set
/// <c>PADURAKSA_PEBBLE_DIRECTORY</c> to its directory URL.
/// </summary>
public sealed class PebbleFixture : IAsyncLifetime
{
    private const string Image = DockerCli.PebbleImage;

    private string? _container;

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

        try
        {
            // Random host ports; no random bad nonces and no authorization reuse, so a run is deterministic.
            var name = $"paduraksa-pebble-{Environment.ProcessId}-{Environment.TickCount64}";
            var (code, output) = await DockerCli.RunAsync(
                "run", "-d", "--rm", "--name", name, "-p", "127.0.0.1::14000",
                "-e", "PEBBLE_VA_ALWAYS_VALID=1", "-e", "PEBBLE_VA_NOSLEEP=1", "-e", "PEBBLE_WFE_NONCEREJECT=0", "-e", "PEBBLE_AUTHZREUSE=0",
                Image, "-config", "/test/config/pebble-config.json");
            if (code != 0)
            {
                SkipReason = $"Docker could not start Pebble: {output.Trim()}";
                return;
            }

            _container = name;
            var (_, port) = await DockerCli.RunAsync("port", name, "14000/tcp");
            // "127.0.0.1:32768" (and, on some hosts, a second line for IPv6)
            var hostPort = port.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            Directory = new Uri($"https://127.0.0.1:{hostPort[(hostPort.LastIndexOf(':') + 1)..]}/dir", UriKind.Absolute);
            SkipReason = await WaitUntilServingAsync(Directory) is { } notReady ? $"Pebble did not become ready in time: {notReady}" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FormatException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            _ = await DockerCli.RunAsync("rm", "-f", _container);
        }
    }

    /// <summary>Waits for the directory to answer; null when it does, else the last thing that went wrong.</summary>
    private static async Task<string?> WaitUntilServingAsync(Uri directory)
    {
        using HttpClient http = new(CreateHandler());
        // Pebble refuses a request without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Paduraksa-Tests/1.0");
        var last = "no answer";
        for (var attempt = 0; attempt < 60; attempt++)
        {
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

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return last;
    }
}
