using System.Net;
using System.Net.Sockets;

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
        int port = FreePort();
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
        int port = FreePort();
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
            ["Paduraksa:Listen:Admin"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
        };

        (int exitCode, _, string error) = await ProbeAsync(TestHost.CreateOptions(settings: settings));

        Assert.Equal(1, exitCode);
        Assert.Contains("did not answer", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutLivenessExplainsAndFails()
    {
        TextWriter output = new StringWriter();
        TextWriter error = new StringWriter();

        int exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(
            TestHost.CreateOptions(), liveness: false, output, error, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("--liveness", error.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ProbeAsync(PaduraksaHostOptions options)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await Paduraksa.Server.Cli.DoctorCommands.LivenessAsync(options, liveness: true, output, error, Ct);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>A port nothing holds right now (the host binds it immediately after).</summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
