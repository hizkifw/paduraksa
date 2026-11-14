using System.CommandLine;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Paduraksa.Application.Yaml;
using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Admin;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Cli;

public sealed class ConfigCommandsTests
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    private const string Yaml = Header + """
        upstreams:
          - name: app
            destinations:
              - address: http://127.0.0.1:9000
        hosts:
          - names: [app.example.test]
            routes:
              - upstream: app
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Run(int Exit, string Output, string Error);

    private static void RestrictToOwner(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<(AdminApiHarness Api, ConfigClientOptions Client, TempDirectory Directory)> StartAsync(string role = "owner")
    {
        var api = await AdminApiHarness.StartAsync(Ct);
        TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, api.TokenValueOf(role) + "\n", Ct);
        RestrictToOwner(tokenFile);
        return (api, new ConfigClientOptions(api.BaseAddress, tokenFile), directory);
    }

    private static async Task<Run> ImportAsync(ConfigClientOptions client, string file, ConfigImportMode mode = ConfigImportMode.Merge, ConfigCommands.ImportRequest? options = null, string standardInput = "")
    {
        using StringWriter output = new();
        using StringWriter error = new();
        using StringReader input = new(standardInput);
        var exit = await ConfigCommands.ImportAsync(client, file, mode, options ?? new ConfigCommands.ImportRequest(), input, output, error, Ct);
        return new Run(exit, output.ToString(), error.ToString());
    }

    private static async Task<Run> ExportAsync(ConfigClientOptions client, string? revision = null, string? outputFile = null)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var exit = await ConfigCommands.ExportAsync(client, revision, outputFile, output, error, Ct);
        return new Run(exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task ImportCommitsOneRevisionAndANoOpImportSaysSo()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            var file = Path.Combine(directory.Path, "config.yaml");
            await File.WriteAllTextAsync(file, Yaml, Ct);

            var first = await ImportAsync(client, file, options: new ConfigCommands.ImportRequest(Message: "from the CLI"));
            var again = await ImportAsync(client, file);

            Assert.Equal(0, first.Exit);
            Assert.Contains("Imported as revision #1: 2 change(s).", first.Output, StringComparison.Ordinal);
            Assert.Equal(0, again.Exit);
            Assert.Contains("Nothing to change", again.Output, StringComparison.Ordinal);
            using var revisions = await api.GetAsync("/api/v1/revisions");
            Assert.Contains("from the CLI", await revisions.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADryRunListsTheChangesAndCommitsNothing()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            var file = Path.Combine(directory.Path, "config.yaml");
            await File.WriteAllTextAsync(file, Yaml, Ct);

            var run = await ImportAsync(client, file, ConfigImportMode.Replace, new ConfigCommands.ImportRequest(DryRun: true));

            Assert.Equal(0, run.Exit);
            Assert.Contains("Dry run: 2 change(s), nothing committed.", run.Output, StringComparison.Ordinal);
            Assert.Contains("  + /upstreams/0", run.Output, StringComparison.Ordinal);
            Assert.Contains("  + /hosts/0", run.Output, StringComparison.Ordinal);
            using var revisions = await api.GetAsync("/api/v1/revisions");
            Assert.DoesNotContain("rev_", await revisions.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StandardInputIsReadAndReplaceDeletesWhatTheYamlLeavesOut()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            await ImportAsync(client, "-", standardInput: Yaml);

            var replaced = await ImportAsync(client, "-", ConfigImportMode.Replace, standardInput: Header + "hosts:\n  - names: [b.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n");

            Assert.Equal(0, replaced.Exit);
            var exported = await ExportAsync(client);
            Assert.DoesNotContain("app.example.test", exported.Output, StringComparison.Ordinal);
            Assert.Contains("b.example.test", exported.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnInvalidYamlPrintsEachErrorWithItsPathAndLineAndExitsNonZero()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            var run = await ImportAsync(client, "-", standardInput: Header + "hosts:\n  - names: [a.example.test]\n    profile: nope\n");

            Assert.Equal(1, run.Exit);
            Assert.Contains("Refused (422", run.Error, StringComparison.Ordinal);
            Assert.Contains("error /hosts/0/profile: ", run.Error, StringComparison.Ordinal);
            Assert.Contains("line 5", run.Error, StringComparison.Ordinal);
            Assert.Empty(run.Output);
        }
    }

    [Fact]
    public async Task ExportWritesToStandardOutputOrAFileAndAPastRevisionCanBeNamed()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            await ImportAsync(client, "-", standardInput: Yaml);
            using var revisions = await api.GetAsync("/api/v1/revisions");
            var id = System.Text.RegularExpressions.Regex.Match(await revisions.Content.ReadAsStringAsync(Ct), "rev_[0-9A-Za-z]+").Value;
            await ImportAsync(client, "-", ConfigImportMode.Replace, standardInput: Header);

            var current = await ExportAsync(client);
            var path = Path.Combine(directory.Path, "out.yaml");
            var toFile = await ExportAsync(client, id, path);

            Assert.Equal(0, current.Exit);
            Assert.DoesNotContain("app.example.test", current.Output, StringComparison.Ordinal);
            Assert.Equal(0, toFile.Exit);
            Assert.Empty(toFile.Error);
            var written = await File.ReadAllTextAsync(path, Ct);
            Assert.Contains("app.example.test", written, StringComparison.Ordinal);
            Assert.False(written.StartsWith('\uFEFF'));
        }
    }

    [Fact]
    public async Task ATokenWithoutWriteCanExportButNotImport()
    {
        var (api, client, directory) = await StartAsync("viewer");
        await using (api)
        using (directory)
        {
            var export = await ExportAsync(client);
            var import = await ImportAsync(client, "-", standardInput: Yaml);

            Assert.Equal(0, export.Exit);
            Assert.Equal(1, import.Exit);
            Assert.Contains("Refused (403", import.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheTokenComesFromTheEnvironmentWhenThereIsNoFileAndIsRequired()
    {
        var (api, client, directory) = await StartAsync();
        await using (api)
        using (directory)
        {
            var viaEnvironment = client with { TokenFile = null, Environment = name => name == ConfigCommands.TokenVariable ? api.TokenValueOf("owner") : null };
            var none = client with { TokenFile = null, Environment = static _ => null };

            Assert.Equal(0, (await ExportAsync(viaEnvironment)).Exit);
            var missing = await ExportAsync(none);
            Assert.Equal(1, missing.Exit);
            Assert.Contains(ConfigCommands.TokenVariable, missing.Error, StringComparison.Ordinal);
            var wrong = await ExportAsync(client with { TokenFile = null, Environment = static _ => "not-a-token" });
            Assert.Equal(1, wrong.Exit);
            Assert.Contains("401", wrong.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnUnreachableServerAnUnreadableFileAndATooLargeYamlAreOneLineEach()
    {
        using TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, "x\n", Ct);
        RestrictToOwner(tokenFile);
        var client = new ConfigClientOptions(new Uri("http://127.0.0.1:1/"), tokenFile);

        var unreachable = await ExportAsync(client);
        var missing = await ImportAsync(client, Path.Combine(directory.Path, "nope.yaml"));
        var large = Path.Combine(directory.Path, "large.yaml");
        await File.WriteAllBytesAsync(large, new byte[YamlLimits.Default.MaxBytes + 1], Ct);
        var tooLarge = await ImportAsync(client, large);

        Assert.Equal(1, unreachable.Exit);
        Assert.Contains("Couldn't reach", unreachable.Error, StringComparison.Ordinal);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("Couldn't read", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, tooLarge.Exit);
        Assert.Contains("largest", tooLarge.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidLocalSettingsExitThreeLikeEveryOtherCommandWhenNoUrlNamesTheServer()
    {
        using TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, "x\n", Ct);
        RestrictToOwner(tokenFile);
        // Port 0 is only valid for tests that say so, so the installation's own settings are invalid.
        var invalid = TestHost.CreateOptions(allowEphemeralPorts: false);

        var export = await ExportAsync(new ConfigClientOptions(null, tokenFile, invalid));
        var import = await ImportAsync(new ConfigClientOptions(null, tokenFile, invalid), Path.Combine(directory.Path, "none.yaml"));

        Assert.Equal(ExitCodes.InvalidConfiguration, export.Exit);
        Assert.Contains("Invalid configuration", export.Error, StringComparison.Ordinal);
        // The file is read first, so a missing one is the failure there; the exit for the settings is the export's.
        Assert.Equal(ExitCodes.Failure, import.Exit);
    }

    [Fact]
    public void TheCommandsAreParsedWithTheirOptionsAndAnUnknownModeIsRefused()
    {
        var root = PaduraksaCli.BuildRootCommand();

        var parsed = root.Parse(["config", "export", "--revision", "rev_x", "--output", "a.yaml", "--url", "https://h:9443", "--token-file", "t"]);
        Assert.Empty(parsed.Errors);
        // The value is read, not only parsed: reading a Uri option without a parser of its own threw.
        Assert.Equal(new Uri("https://h:9443"), parsed.GetValue<Uri>("--url"));
        Assert.NotEmpty(root.Parse(["config", "export", "--url", "127.0.0.1:9443"]).Errors);
        Assert.NotEmpty(root.Parse(["config", "export", "--url", "ftp://h"]).Errors);
        Assert.Empty(root.Parse(["config", "import", "c.yaml", "--mode", "replace", "--dry-run", "--message", "m", "--base-revision", "rev_x", "--force"]).Errors);
        Assert.NotEmpty(root.Parse(["config", "import", "c.yaml", "--mode", "overwrite"]).Errors);
        Assert.NotEmpty(root.Parse(["config", "import"]).Errors);
        Assert.NotEmpty(root.Parse(["config", "bogus"]).Errors);
    }

    [Fact]
    public void TheDefaultAddressIsThisInstallationsOwnAdminListener()
    {
        var options = PaduraksaHost.LoadOptions(new PaduraksaHostOptions { ContentRootPath = Path.GetTempPath() });

        var address = ConfigCommands.OwnAdminAddress(options);

        Assert.True(address.IsLoopback);
        Assert.Equal(options.Listen.Admin, address.Port);
        Assert.Equal("https", address.Scheme);
    }

    [Fact]
    public async Task ATokenIsNotSentOverPlainHttpToAnAddressThatIsntLoopbackUnlessAllowed()
    {
        using TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, "x\n", Ct);
        RestrictToOwner(tokenFile);

        var refused = await ExportAsync(new ConfigClientOptions(new Uri("http://admin.invalid:9/"), tokenFile));
        var allowed = await ExportAsync(new ConfigClientOptions(new Uri("http://admin.invalid:9/"), tokenFile, AllowInsecureHttp: true));
        var loopback = await ExportAsync(new ConfigClientOptions(new Uri("http://127.0.0.1:1/"), tokenFile));
        var localhost = await ExportAsync(new ConfigClientOptions(new Uri("http://localhost:1/"), tokenFile));

        Assert.Equal(1, refused.Exit);
        Assert.Contains("over plain HTTP", refused.Error, StringComparison.Ordinal);
        Assert.Contains("--allow-insecure-http", refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Couldn't reach", refused.Error, StringComparison.Ordinal);
        Assert.Contains("Couldn't reach", allowed.Error, StringComparison.Ordinal);
        Assert.Contains("Couldn't reach", loopback.Error, StringComparison.Ordinal);
        Assert.Contains("Couldn't reach", localhost.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenFileOtherUsersCanReadIsWarnedAboutAndAPrivateOneIsNot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, "x\n", Ct);
        var client = new ConfigClientOptions(new Uri("http://127.0.0.1:1/"), tokenFile);

        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var shared = await ExportAsync(client);
        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var world = await ExportAsync(client);
        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var owner = await ExportAsync(client);

        Assert.Contains("can be read by other users", shared.Error, StringComparison.Ordinal);
        Assert.Contains("can be read by other users", world.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("can be read by other users", owner.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpsAddressThatIsntLoopbackMustPresentACertificateTheSystemTrusts()
    {
        // A server with a self-signed certificate on every address; reached over the machine's own non-loopback address the CLI must refuse its certificate,
        // and over loopback (where the admin's own-CA certificate can't be checked) it talks to it.
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(static i => i.GetIPProperties().UnicastAddresses)
            .Select(static a => a.Address)
            .FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (address is null)
        {
            Assert.Skip("This machine has no non-loopback IPv4 address to reach a test server on.");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=cli-test", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        san.AddIpAddress(address);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using TcpListener listener = new(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource stop = new();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                    await using SslStream tls = new(tcp.GetStream());
                    await tls.AuthenticateAsServerAsync(certificate);
                    var buffer = new byte[4096];
                    _ = await tls.ReadAsync(buffer, stop.Token);
                    await tls.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The client that refuses the certificate drops the handshake: expected.
                }
            }
        }, Ct);

        using TempDirectory directory = new();
        var tokenFile = Path.Combine(directory.Path, "token");
        await File.WriteAllTextAsync(tokenFile, "x\n", Ct);
        RestrictToOwner(tokenFile);
        try
        {
            var external = await ExportAsync(new ConfigClientOptions(new Uri($"https://{address}:{port}/"), tokenFile));
            var loopback = await ExportAsync(new ConfigClientOptions(new Uri($"https://127.0.0.1:{port}/"), tokenFile));

            Assert.Equal(1, external.Exit);
            Assert.Contains("Couldn't reach", external.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Refused (401", external.Error, StringComparison.Ordinal);
            Assert.Equal(1, loopback.Exit);
            Assert.Contains("Refused (401", loopback.Error, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
    }
}
