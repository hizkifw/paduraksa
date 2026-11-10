
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Cli;

public sealed class DoctorCommandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LivenessUsesTheLoopbackPlainHttpFallbackWhenItIsOn()
    {
        // The loopback listener treats 0 as "off", so it needs a number; the helper retries the whole start with a new port
        // when one is taken between the probe and the bind, and the settings carry the port that won.
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Admin"] = "0",
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
        };

        var (host, _) = await TestPorts.StartOnFreePortAsync(port =>
        {
            settings["Paduraksa:Listen:AdminLoopbackHttp"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return TestHost.Create(settings: settings);
        });

        await using var started = host;
        try
        {
            var (exitCode, output, error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

            Assert.Equal(0, exitCode);
            Assert.Equal("healthy", output.Trim());
            Assert.Empty(error.Trim());
        }
        finally
        {
            await started.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LivenessUsesHttpsToTheAdminPortOtherwise()
    {
        // Bound at port 0 and read back: a port probed first can be taken by another test between the probe and the bind.
        await using var host = TestHost.Create();
        await host.StartAsync(Ct);
        try
        {
            var port = TestHost.Endpoint(host, HostRole.Admin, "https").Port;
            Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Paduraksa:Listen:Admin"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
            };

            // The admin certificate chains to the local CA the probe process never loads: the call trusts loopback for itself.
            Assert.Equal(0, (await ProbeAsync(TestHost.CreateOptions(settings: settings))).ExitCode);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LivenessFailsWhenNothingListens()
    {
        // A port held bound but never listening for as long as the probe runs: a probed one could be taken meanwhile.
        using var dead = TestPorts.ReserveDeadPort();
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Admin"] = dead.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
        };

        var (exitCode, _, error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

        Assert.Equal(1, exitCode);
        Assert.Contains("did not answer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutLivenessExplainsAndFails()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        var exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(
            TestHost.CreateOptions(), liveness: false, output, error, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("--liveness", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutTheOptionParsesAndExplains()
    {
        // GetRequiredValue on an absent bool option threw; GetValue keeps `paduraksa doctor` a parse-level success that
        // exits 1 with the usage line.
        var root = Paduraksa.Server.Cli.PaduraksaCli.BuildRootCommand();
        var parse = root.Parse(["doctor"]);
        Assert.Empty(parse.Errors);

        using StringWriter output = new();
        using StringWriter error = new();
        var exitCode = await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error, ProcessTerminationTimeout = null }, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("--liveness", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A503AnswerIsUnhealthy()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            using var stream = client.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, Ct);
            await stream.WriteAsync("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), Ct);
        }, Ct);

        try
        {
            Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Paduraksa:Listen:Admin"] = "0",
                ["Paduraksa:Listen:AdminLoopbackHttp"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            var (exitCode, _, error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

            Assert.Equal(1, exitCode);
            Assert.Contains("503", error, StringComparison.Ordinal);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("0.0.0.0", 0, "https://127.0.0.1:9443/healthz")]
    [InlineData("::", 0, "https://[::1]:9443/healthz")]
    [InlineData("192.168.1.5", 0, "https://192.168.1.5:9443/healthz")]
    [InlineData("127.0.0.1", 0, "https://127.0.0.1:9443/healthz")]
    [InlineData("192.168.1.5", 9080, "http://127.0.0.1:9080/healthz")]
    public void HealthUrisFollowTheListenerLayout(string adminBind, int loopbackHttpPort, string expected)
    {
        PaduraksaOptions options = new()
        {
            Listen = new PaduraksaListenOptions
            {
                Admin = 9443,
                AdminBind = adminBind,
                AdminLoopbackHttp = loopbackHttpPort,
            },
        };

        Assert.Equal(expected, Paduraksa.Server.Cli.DoctorCommands.HealthUris(options)[0].AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void ADualModeBindAlsoTriesIPv4()
    {
        PaduraksaOptions options = new() { Listen = new PaduraksaListenOptions { Admin = 9443, AdminBind = "::" } };

        Assert.Equal(
            ["https://[::1]:9443/healthz", "https://127.0.0.1:9443/healthz"],
            Paduraksa.Server.Cli.DoctorCommands.HealthUris(options).Select(u => u.AbsoluteUri.TrimEnd('/')));
    }

    private static async Task<(int ExitCode, string Output, string Error)> ProbeAsync(PaduraksaHostOptions options)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(options, liveness: true, output, error, Ct);
        return (exitCode, output.ToString(), error.ToString());
    }
}
