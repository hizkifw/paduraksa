using System.CommandLine;
using System.Security.Cryptography;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Cli;

/// <summary><c>paduraksa vault rotate-kek</c> against a real data directory the vault created.</summary>
public sealed class VaultCommandsTests
{
    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    [Fact]
    public async Task RotateKekRewrapsTheVaultAndTheServiceStartsWithTheNewKey()
    {
        string data = TestDataDirectories.NewPath();
        string fingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            fingerprint = (await first.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["rot.test"]), Origin, Ct)).Certificate!.Sha256Fingerprint;
            await CommitAsync(first, new UpsertHostCommand(RespondHost("r", "rot.test", "ok")), Ct);
        }

        string keyFile = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        string oldKey = await File.ReadAllTextAsync(keyFile, Ct);
        string newKeyPath = Path.Combine(data, "new.key");
        string newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(newKeyPath, newKey + "\n", Ct);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), newKeyPath, TextReader.Null, standardInputIsTerminal: false, output, error, Ct);

        Assert.True(exitCode == ExitCodes.Success, error.ToString());
        Assert.Equal(newKey, (await File.ReadAllTextAsync(keyFile, Ct)).Trim());
        Assert.Equal(oldKey, await File.ReadAllTextAsync(keyFile + ".old", Ct));
        Assert.DoesNotContain(newKey, output.ToString() + error, StringComparison.Ordinal);
        Assert.DoesNotContain(oldKey.Trim(), output.ToString() + error, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile + ".old"));
        }

        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);
        Assert.Equal(fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, "rot.test")).Certificate));
    }

    [Fact]
    public async Task RotateKekReadsTheKeyFromStandardInputAndKeepsAnEarlierBackupSafe()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using StringWriter output = new();
        using StringWriter error = new();

        Assert.Equal(ExitCodes.Success, await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(newKey + "\n"), standardInputIsTerminal: false, output, error, Ct));

        // A second rotation would overwrite master.key.old, the only copy of the previous key: refused until it is deleted.
        string another = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string keyFile = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        Assert.Equal(ExitCodes.Failure, await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(another), standardInputIsTerminal: false, output, error, Ct));
        Assert.Contains("master.key.old", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(newKey, (await File.ReadAllTextAsync(keyFile, Ct)).Trim());
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public async Task RotateKekRefusesAKeyThatIsNotThirtyTwoBytesAndChangesNothing(string badKey)
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string keyFile = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        string before = await File.ReadAllTextAsync(keyFile, Ct);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(badKey), standardInputIsTerminal: false, output, error, Ct);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(before, await File.ReadAllTextAsync(keyFile, Ct));
        Assert.False(File.Exists(keyFile + ".old"));
        Assert.DoesNotContain(badKey, output.ToString() + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateKekRefusesStandardInputThatIsATerminal()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(
            TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), standardInputIsTerminal: true, TextWriter.Null, error, Ct);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("terminal", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(data, "master.key.old")));
    }

    [Fact]
    public async Task AFailedRotationPutsTheOldKeyBackAndLeavesNoBackup()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string keyFile = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        string current = await File.ReadAllTextAsync(keyFile, Ct);
        using StringWriter error = new();

        // The "new" key is the one in use: the vault refuses the rotation after master.key was already replaced.
        int exitCode = await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(current), standardInputIsTerminal: false, TextWriter.Null, error, Ct);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("already in use", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(current, await File.ReadAllTextAsync(keyFile, Ct));
        Assert.False(File.Exists(keyFile + ".old"));
        Assert.False(File.Exists(keyFile + ".new"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        }
    }

    [Fact]
    public async Task AStaleTemporaryFileOfAnInterruptedRunIsReplacedNotTrusted()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string keyFile = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        await File.WriteAllTextAsync(keyFile + ".new", "junk from an earlier crash", Ct);
        string newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(newKey), standardInputIsTerminal: false, TextWriter.Null, error, Ct);

        Assert.True(exitCode == ExitCodes.Success, error.ToString());
        Assert.Equal(newKey, (await File.ReadAllTextAsync(keyFile, Ct)).Trim());
        Assert.False(File.Exists(keyFile + ".new"));
    }

    [Fact]
    public async Task AGroupReadableKeyFileIsUsedWithAWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string newKeyPath = Path.Combine(data, "new.key");
        await File.WriteAllTextAsync(newKeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Ct);
        File.SetUnixFileMode(newKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(TestHost.CreateOptions(dataDirectory: data), newKeyPath, TextReader.Null, standardInputIsTerminal: false, TextWriter.Null, error, Ct);

        Assert.True(exitCode == ExitCodes.Success, error.ToString());
        Assert.Contains("can be read by other users", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAllZeroKeyIsNotAKeyEitherWayTheVaultsParserSaysSo()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        string before = await File.ReadAllTextAsync(Path.Combine(data, "master.key"), Ct);
        using StringWriter error = new();

        int exitCode = await VaultCommands.RotateKekAsync(
            TestHost.CreateOptions(dataDirectory: data), "-", new StringReader(Convert.ToBase64String(new byte[32])), standardInputIsTerminal: false, TextWriter.Null, error, Ct);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(data, "master.key"), Ct));
    }

    [Fact]
    public void RotateKekTakesTheKeyFromAFileOrStandardInputNeverAsAValue()
    {
        Command vault = PaduraksaCli.BuildRootCommand().Subcommands.Single(c => c.Name == "vault");
        Command rotate = vault.Subcommands.Single(c => c.Name == "rotate-kek");

        Assert.Equal(["--new-key-file"], rotate.Options.Select(o => o.Name).Where(n => n != "--help" && n != "--version"));
        Assert.Empty(PaduraksaCli.BuildRootCommand().Parse(["vault", "rotate-kek", "--new-key-file", "-"]).Errors);
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(["vault", "rotate-kek"]).Errors);
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(["vault", "rotate-kek", "--new-key", "AAAA"]).Errors);
    }
}
