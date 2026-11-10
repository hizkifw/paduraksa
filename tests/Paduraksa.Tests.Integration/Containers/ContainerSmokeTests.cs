using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Containers;

/// <summary>
/// The container smoke test: builds the image, runs it on an empty volume with random host ports, and checks
/// the healthcheck, the setup banner, the admin setup API, the public listeners, HTTP/3 loading and a clean stop within the
/// grace period. It is slow (an image build and two 30-second health probes), so it is <b>off by default</b>: set
/// <c>PADURAKSA_CONTAINER_TESTS=1</c> and filter on the trait if you want only this:
/// <c>dotnet test --project tests/Paduraksa.Tests.Integration -- --filter-trait "Category=Container"</c>.
/// </summary>
[Trait("Category", "Container")]
public sealed class ContainerSmokeTests
{
    private const string SetupBanner = "Paduraksa first-run setup";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheImageStartsHealthyServesTheAdminAndPublicListenersAndStopsCleanly(bool dropCapabilities)
    {
        Assert.SkipWhen(
            !string.Equals(Environment.GetEnvironmentVariable("PADURAKSA_CONTAINER_TESTS"), "1", StringComparison.Ordinal),
            "Set PADURAKSA_CONTAINER_TESTS=1 to build and run the container image (slow).");
        Assert.SkipWhen(!await DockerIsAvailableAsync(), "Docker is not available.");

        var repository = RepositoryRoot();
        var suffix = $"{Environment.ProcessId}-{Environment.TickCount64}-{(dropCapabilities ? "caps" : "default")}";
        var image = $"paduraksa-smoke:{suffix}";
        var container = $"paduraksa-smoke-{suffix}";
        var volume = $"paduraksa-smoke-{suffix}";

        try
        {
            var (buildCode, buildOutput) = await DockerCli.RunAsync(
                TimeSpan.FromMinutes(30), "build", "--tag", image, "--file", Path.Combine(repository, "Dockerfile"), repository);
            Assert.True(buildCode == 0, $"docker build failed ({buildCode}):\n{buildOutput}");

            List<string> run = ["run", "--detach", "--name", container];
            if (dropCapabilities)
            {
                // The documented hardening path: dropping every capability requires NET_BIND_SERVICE back, or exec of the
                // setcap'd apphost fails with EPERM.
                run.AddRange(["--cap-drop", "ALL", "--cap-add", "NET_BIND_SERVICE"]);
            }

            run.AddRange([
                "-p", "127.0.0.1::80",
                "-p", "127.0.0.1::443",
                "-p", "127.0.0.1::443/udp",
                "-p", "127.0.0.1::9443",
                "-v", $"{volume}:/data",
                image,
            ]);

            var (runCode, runOutput) = await DockerCli.RunAsync([.. run]);
            Assert.True(runCode == 0, $"docker run failed ({runCode}):\n{runOutput}");

            var adminPort = await HostPortAsync(container, "9443/tcp");
            var httpPort = await HostPortAsync(container, "80/tcp");
            var tlsPort = await HostPortAsync(container, "443/tcp");

            // 1. The image's own HEALTHCHECK (paduraksa doctor --liveness) turns healthy. The interval is 30 s.
            var health = await WaitForHealthAsync(container);
            Assert.Equal("healthy", health);

            // 2. The setup banner is in the logs (read with `docker logs`).
            var (_, logs) = await DockerLogsAsync(container);
            Assert.Contains(SetupBanner, logs, StringComparison.Ordinal);
            Assert.Contains("Setup token:", logs, StringComparison.Ordinal);

            // 3. libmsquic loaded: the server logs the positive HTTP/3 line only when QUIC is available.
            Assert.Contains("HTTP/3 is on", logs, StringComparison.Ordinal);
            // The framework's "no XML encryptor" warning is filtered: the vault seals the admin's ring, and the public app
            // uses an ephemeral provider that never touches the disk.
            Assert.DoesNotContain("No XML encryptor configured", logs, StringComparison.Ordinal);

            // 4. The documented way to reprint the token inside the container works while the service runs.
            var (tokenCode, tokenOutput) = await DockerCli.RunAsync("exec", container, "paduraksa", "setup-token");
            Assert.True(tokenCode == 0, $"docker exec paduraksa setup-token failed ({tokenCode}): {tokenOutput}");
            Assert.Contains(SetupBanner, tokenOutput, StringComparison.Ordinal);

            // No Data Protection key XML on the disk: the admin's ring is sealed in vault.db, the public app's is in memory.
            var (xmlCode, xml) = await DockerCli.RunAsync("exec", container, "find", "/data", "-name", "*.xml");
            Assert.True(xmlCode == 0, $"find /data failed: {xml}");
            Assert.True(string.IsNullOrWhiteSpace(xml), $"a plain key file is on the data volume:\n{xml}");

            // 5. The admin setup API answers over HTTPS (the local CA is not trusted by this test, which is expected).
            using var client = CreateClient();
            using var setup = await client.GetAsync(new Uri($"https://127.0.0.1:{adminPort}/api/v1/setup"), Ct);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

            // 6. The public listeners answer: HTTP/1.x on 80 (404 with an empty config is an answer), and 443 accepts a
            //    connection (a fresh install has no public certificate yet, so a TLS request would fail by design).
            using var publicHttp = await client.GetAsync(new Uri($"http://127.0.0.1:{httpPort}/"), Ct);
            Assert.True((int)publicHttp.StatusCode is >= 200 and < 600, $"port 80 answered {(int)publicHttp.StatusCode}.");
            using (TcpClient tls = new())
            {
                await tls.ConnectAsync(IPAddress.Loopback, tlsPort, Ct);
                Assert.True(tls.Connected, "port 443 did not accept a connection.");
            }

            // 7. A clean stop: SIGTERM, the ordered drain, exit code 0, inside the 35-second grace period.
            var stopwatch = Stopwatch.StartNew();
            var (stopCode, stopOutput) = await DockerCli.RunAsync("stop", "--time", "35", container);
            stopwatch.Stop();
            Assert.True(stopCode == 0, $"docker stop failed: {stopOutput}");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(35), $"the stop took {stopwatch.Elapsed}, past the grace period.");
            var (_, exitCode) = await DockerCli.RunAsync("inspect", "--format", "{{.State.ExitCode}}", container);
            Assert.Equal("0", exitCode.Trim());
        }
        finally
        {
            await CleanupAsync(container, volume, image);
        }
    }

    private static async Task CleanupAsync(string container, string volume, string image)
    {
        foreach (var arguments in new[]
        {
            new[] { "rm", "--force", container },
            new[] { "volume", "rm", "--force", volume },
            new[] { "image", "rm", "--force", image },
        })
        {
            try
            {
                _ = await DockerCli.RunAsync(arguments);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                // Best effort: a failed cleanup must not hide the test's own result.
            }
        }
    }

    private static async Task<bool> DockerIsAvailableAsync()
    {
        try
        {
            var (code, _) = await DockerCli.RunAsync(TimeSpan.FromSeconds(30), "info", "--format", "{{.ServerVersion}}");
            return code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<int> HostPortAsync(string container, string containerPort)
    {
        var (code, output) = await DockerCli.RunAsync("port", container, containerPort);
        Assert.True(code == 0, $"docker port {containerPort} failed: {output}");
        var mapping = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    private static async Task<string> WaitForHealthAsync(string container)
    {
        var status = "starting";
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var (code, output) = await DockerCli.RunAsync("inspect", "--format", "{{.State.Health.Status}}", container);
            if (code != 0)
            {
                var (_, logs) = await DockerLogsAsync(container);
                Assert.Fail($"docker inspect failed while waiting for health: {output}\n{logs}");
            }

            status = output.Trim();
            if (status is "healthy" or "unhealthy")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        if (status != "healthy")
        {
            var (_, logs) = await DockerLogsAsync(container);
            Assert.Fail($"the healthcheck did not turn healthy (last status: {status}).\n{logs}");
        }

        return status;
    }

    private static async Task<(int Code, string Output)> DockerLogsAsync(string container) =>
        await DockerCli.RunAsync("logs", container);

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions =
            {
                // The admin certificate chains to the container's local CA, which this test process does not know; it only
                // checks that the endpoint answers.
#pragma warning disable CA5359 // Deliberate: the smoke test is about reachability, not the chain.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
            },
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>The repository root (the directory holding <c>Paduraksa.slnx</c>), found from the test's base directory.</summary>
    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Paduraksa.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Paduraksa.slnx) above the test's base directory.");
    }
}
