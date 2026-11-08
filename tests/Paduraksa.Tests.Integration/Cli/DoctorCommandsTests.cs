
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Cli;

/// <summary>
/// <c>paduraksa doctor --liveness</c> (PLAN §11.4, §16): the container healthcheck, against a real admin listener, over the
/// loopback plain-HTTP fallback when it is on and over HTTPS to the admin port otherwise.
/// </summary>
public sealed class DoctorCommandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LivenessUsesTheLoopbackPlainHttpFallbackWhenItIsOn()
    {
        int port = TestPorts.FreePort();
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Admin"] = "0",
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
            ["Paduraksa:Listen:AdminLoopbackHttp"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await using PaduraksaHost host = TestHost.Create(settings: settings);
        await host.StartAsync(Ct);
        try
        {
            (int exitCode, string output, string error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

            Assert.Equal(0, exitCode);
            Assert.Equal("healthy", output.Trim());
            Assert.Empty(error.Trim());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LivenessUsesHttpsToTheAdminPortOtherwise()
    {
        int port = TestPorts.FreePort();
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Admin"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
        };

        await using PaduraksaHost host = TestHost.Create(settings: settings);
        await host.StartAsync(Ct);
        try
        {
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
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Admin"] = TestPorts.FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
        };

        (int exitCode, _, string error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

        Assert.Equal(1, exitCode);
        Assert.Contains("did not answer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutLivenessExplainsAndFails()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(
            TestHost.CreateOptions(), liveness: false, output, error, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("--liveness", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutTheOptionParsesAndExplains()
    {
        // GetRequiredValue on an absent bool option threw; GetValue keeps `paduraksa doctor` a parse-level success that
        // exits 1 with the usage line.
        RootCommand root = Paduraksa.Server.Cli.PaduraksaCli.BuildRootCommand();
        ParseResult parse = root.Parse(["doctor"]);
        Assert.Empty(parse.Errors);

        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error, ProcessTerminationTimeout = null }, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("--liveness", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A503AnswerIsUnhealthy()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];
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

            (int exitCode, _, string error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

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
        int exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(options, liveness: true, output, error, Ct);
        return (exitCode, output.ToString(), error.ToString());
    }
}
