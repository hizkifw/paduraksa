using System.Net;

using Paduraksa.Tests.Acme.Dns;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// A <a href="https://github.com/letsencrypt/pebble">Pebble</a> ACME test CA that <b>really validates DNS-01</b>: it is started in Docker without
/// <c>PEBBLE_VA_ALWAYS_VALID</c>, on a network of its own with a <c>pebble-challtestsrv</c> container as its resolver, so a challenge passes only if the TXT
/// record is in that server when Pebble asks (<see cref="SetTxtAsync"/>, <see cref="SetCnameAsync"/> write there through its management API). The container
/// and the host share nothing but published ports, so a host firewall between them can't matter. The propagation check asks <see cref="Dns"/>, a fake
/// authoritative server (challtestsrv doesn't set the AA bit the check requires) that the tests keep in step. Skips like <see cref="PebbleFixture"/> without Docker.
/// </summary>
public sealed class PebbleDnsFixture : IAsyncLifetime
{
    private const string ChallengeServerImage = DockerCli.ChallengeTestServerImage;

    private readonly List<string> _containers = [];
    private string? _network;
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
            var suffix = $"{Environment.ProcessId}-{Environment.TickCount64}";
            _network = $"paduraksa-pebble-dns-{suffix}";
            if (await DockerCli.RunAsync("network", "create", _network) is { ExitCode: not 0 } noNetwork)
            {
                SkipReason = $"Docker could not create a network: {noNetwork.Output.Trim()}";
                return;
            }

            var server = $"paduraksa-challtestsrv-{suffix}";
            var (serverCode, serverOutput) = await DockerCli.RunAsync(
                "run", "-d", "--rm", "--name", server, "--network", _network, "-p", "127.0.0.1::8055",
                ChallengeServerImage, "-http01", string.Empty, "-https01", string.Empty, "-tlsalpn01", string.Empty, "-doh", string.Empty);
            if (serverCode != 0)
            {
                SkipReason = $"Docker could not start the challenge test server: {serverOutput.Trim()}";
                return;
            }

            _containers.Add(server);
            var (_, address) = await DockerCli.RunAsync("inspect", "-f", $"{{{{(index .NetworkSettings.Networks \"{_network}\").IPAddress}}}}", server);
            var (_, managementPort) = await DockerCli.RunAsync("port", server, "8055/tcp");
            _management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{LastPort(managementPort)}/") };

            var pebble = $"paduraksa-pebble-dns-{suffix}";
            var (started, output) = await DockerCli.RunAsync(
                "run", "-d", "--rm", "--name", pebble, "--network", _network, "-p", "127.0.0.1::14000",
                "-e", "PEBBLE_VA_NOSLEEP=1", "-e", "PEBBLE_WFE_NONCEREJECT=0", "-e", "PEBBLE_AUTHZREUSE=0",
                DockerCli.PebbleImage, "-config", "/test/config/pebble-config.json", "-dnsserver", $"{address.Trim()}:8053");
            if (started != 0)
            {
                SkipReason = $"Docker could not start Pebble: {output.Trim()}";
                return;
            }

            _containers.Add(pebble);
            var (_, port) = await DockerCli.RunAsync("port", pebble, "14000/tcp");
            Directory = new Uri($"https://127.0.0.1:{LastPort(port)}/dir", UriKind.Absolute);
            SkipReason = await WaitUntilServingAsync(Directory) is { } notReady ? $"Pebble did not become ready in time: {notReady}" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FormatException or System.Net.Sockets.SocketException)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
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

    private static string LastPort(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return line[(line.LastIndexOf(':') + 1)..];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            _ = await DockerCli.RunAsync("rm", "-f", container);
        }

        if (_network is not null)
        {
            _ = await DockerCli.RunAsync("network", "rm", _network);
        }

        _management?.Dispose();
        Dns?.Dispose();
    }

    private static async Task<string?> WaitUntilServingAsync(Uri directory)
    {
        using HttpClient http = new(PebbleFixture.CreateHandler());
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
                last = ex.GetBaseException().Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return last;
    }
}
