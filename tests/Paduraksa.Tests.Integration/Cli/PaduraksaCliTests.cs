using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Cli;

public sealed class PaduraksaCliTests
{
    [Theory]
    [InlineData]
    [InlineData("serve")]
    public void NoArgumentsAndServeBothRunTheServer(params string[] args)
    {
        ParseResult result = PaduraksaCli.BuildRootCommand().Parse(args);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Action);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("serve", "--unknown")]
    public void UnknownCommandsAndOptionsAreRejected(params string[] args)
    {
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(args).Errors);
    }

    [Fact]
    public async Task MalformedAppsettingsIsOneLineAndInvalidConfiguration()
    {
        using TempDirectory contentRoot = new();
        await File.WriteAllTextAsync(Path.Combine(contentRoot.Path, "appsettings.json"), "{ \"Paduraksa\": ", TestContext.Current.CancellationToken);
        using StringWriter error = new();

        int exitCode = await PaduraksaCli.ServeAsync(Options(contentRoot.Path), error, includeStackTrace: false, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.InvalidConfiguration, exitCode);
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("Invalid configuration: ", line, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", line, StringComparison.Ordinal);
        Assert.Contains(PaduraksaCli.DebugVariable, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherStartupFailuresAreOneLineAndStartupFailed()
    {
        using TempDirectory contentRoot = new();
        using StringWriter error = new();
        PaduraksaHostOptions options = Options(contentRoot.Path, static _ => throw new IOException("config source unreadable", new UnauthorizedAccessException("permission denied")));

        int exitCode = await PaduraksaCli.ServeAsync(options, error, includeStackTrace: false, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.StartupFailed, exitCode);
        string line = Assert.Single(Lines(error));
        Assert.StartsWith("Paduraksa failed to start: config source unreadable ---> permission denied", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StackTraceIsPrintedOnlyWhenAskedFor()
    {
        using TempDirectory contentRoot = new();
        using StringWriter error = new();
        PaduraksaHostOptions options = Options(contentRoot.Path, static _ => throw new IOException("config source unreadable"));

        int exitCode = await PaduraksaCli.ServeAsync(options, error, includeStackTrace: true, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.StartupFailed, exitCode);
        Assert.Contains("System.IO.IOException: config source unreadable", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(" at ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposalFailureKeepsAFailedRunsExitCode()
    {
        // The admin port is taken, so the start fails (2); disposing the shared services then throws too.
        using TcpListener blocker = new(IPAddress.Loopback, 0);
        blocker.Start();
        PaduraksaHostOptions options = TestHost.CreateOptions(
            settings: new Dictionary<string, string?> { ["Paduraksa:Listen:Admin"] = ((IPEndPoint)blocker.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) },
            configureShared: static shared => shared.Add(new ThrowsOnDispose()));
        using StringWriter error = new();

        int exitCode = await PaduraksaCli.ServeAsync(options, error, includeStackTrace: false, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.StartupFailed, exitCode);
        Assert.StartsWith("Paduraksa failed to shut down cleanly: ", Assert.Single(Lines(error)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposalFailureTurnsACleanRunIntoAFailure()
    {
        // Cancelled before it starts: a requested stop (0), but disposing the shared services throws.
        PaduraksaHostOptions options = TestHost.CreateOptions(configureShared: static shared => shared.Add(new ThrowsOnDispose()));
        using StringWriter error = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        int exitCode = await PaduraksaCli.ServeAsync(options, error, includeStackTrace: false, cancelled.Token);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.StartsWith("Paduraksa failed to shut down cleanly: ", Assert.Single(Lines(error)), StringComparison.Ordinal);
    }

    private sealed class ThrowsOnDispose : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }

    private static PaduraksaHostOptions Options(string contentRootPath, Action<Microsoft.Extensions.Configuration.IConfigurationBuilder>? configure = null) => new()
    {
        EnvironmentName = "Testing",
        ContentRootPath = contentRootPath,
        ConfigureConfiguration = configure,
    };

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
