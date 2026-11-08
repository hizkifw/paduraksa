using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Cli;

/// <summary>
/// First-run setup, the real authorizers and <c>paduraksa setup-token</c> / <c>admin reset-password</c> / <c>admin disable-2fa</c>,
/// against a real data directory and a running server.
/// </summary>
public sealed class IdentityCommandsTests
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientContext Client => new(Channel.Dashboard, System.Net.IPAddress.Loopback, "test");

    [Fact]
    public async Task TheServerWritesTheSetupTokenWhileSetupIsIncompleteAndDeletesItAfterwards()
    {
        string data = TestDataDirectories.NewPath();
        string tokenFile = Path.Combine(data, SetupService.TokenFileName);
        await using PaduraksaHost host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(Ct);

        Assert.False(await host.Shared.Setup.IsCompleteAsync(Ct));
        string token = (await File.ReadAllTextAsync(tokenFile, Ct)).Trim();
        Assert.Equal(43, token.Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
        }

        Assert.Equal(SetupOutcome.InvalidToken, (await host.Shared.Setup.CompleteSetupAsync("nope", "root", Password, null, Client, Ct)).Outcome);
        SetupResult done = await host.Shared.Setup.CompleteSetupAsync(token, "root", Password, null, Client, Ct);

        Assert.Equal(SetupOutcome.Succeeded, done.Outcome);
        Assert.False(File.Exists(tokenFile));
        Assert.Equal(LoginStatus.Succeeded, (await host.Shared.Login.LoginAsync("root", Password, Client, Ct)).Status);
    }

    [Fact]
    public async Task ARestartAfterSetupLeavesNoToken()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
            string token = (await File.ReadAllTextAsync(Path.Combine(data, SetupService.TokenFileName), Ct)).Trim();
            await first.Shared.Setup.CompleteSetupAsync(token, "root", Password, null, Client, Ct);
        }

        await using PaduraksaHost second = TestHost.Create(dataDirectory: data);
        await second.StartAsync(Ct);

        Assert.True(await second.Shared.Setup.IsCompleteAsync(Ct));
        Assert.False(File.Exists(Path.Combine(data, SetupService.TokenFileName)));
    }

    [Fact]
    public async Task TheServerRunsConfigCommandsOnlyForOriginsWithTheWritePermission()
    {
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(Ct);
        string token = (await File.ReadAllTextAsync(Path.Combine(host.Options.DataDirectory, SetupService.TokenFileName), Ct)).Trim();
        UserSummary owner = (await host.Shared.Setup.CompleteSetupAsync(token, "root", Password, null, Client, Ct)).Owner!;
        UserSummary viewer = (await host.Shared.Users.CreateAsync(CommandOrigin.Cli, new("viewer", null, Password, [Role.Viewer]), Ct)).User!;

        ConfigCommandResult denied = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(RespondHost("h", "a.test", "ok")), Client.ToOrigin(Actor.ForUser(viewer.Id)), Ct);
        ConfigCommandResult allowed = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(RespondHost("h", "a.test", "ok")), Client.ToOrigin(Actor.ForUser(owner.Id)), Ct);

        Assert.Equal(ConfigCommandStatus.Denied, denied.Status);
        Assert.Equal(ConfigCommandStatus.Committed, allowed.Status);
    }

    [Fact]
    public async Task SetupTokenCommandPrintsTheTokenThenSaysSetupIsComplete()
    {
        string data = TestDataDirectories.NewPath();
        using StringWriter output = new();
        using StringWriter error = new();

        int first = await IdentityCommands.SetupTokenAsync(TestHost.CreateOptions(dataDirectory: data), output, error, Ct);
        string token = (await File.ReadAllTextAsync(Path.Combine(data, SetupService.TokenFileName), Ct)).Trim();
        int again = await IdentityCommands.SetupTokenAsync(TestHost.CreateOptions(dataDirectory: data), output, error, Ct);

        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (first, again));
        Assert.Equal(3, output.ToString().Split(token).Length); // printed by each run: the file's token, unchanged
        Assert.Equal(string.Empty, error.ToString());

        await using (PaduraksaHost host = TestHost.Create(dataDirectory: data))
        {
            await host.StartAsync(Ct);
            await host.Shared.Setup.CompleteSetupAsync(token, "root", Password, null, Client, Ct);
        }

        using StringWriter after = new();
        Assert.Equal(ExitCodes.Success, await IdentityCommands.SetupTokenAsync(TestHost.CreateOptions(dataDirectory: data), after, error, Ct));
        Assert.Contains("complete", after.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, after.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPasswordReadsTheNewPasswordFromStandardInputAndIsAuditedAsTheCli()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost host = TestHost.Create(dataDirectory: data))
        {
            await host.StartAsync(Ct);
            await host.Shared.Users.CreateAsync(CommandOrigin.Cli, new("alice", null, Password, [Role.Owner]), Ct);
        }

        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await IdentityCommands.ResetPasswordAsync(
            TestHost.CreateOptions(dataDirectory: data), "ALICE", new StringReader("a brand new passphrase\n"), standardInputIsTerminal: false, output, error, Ct);

        Assert.True(exit == ExitCodes.Success, error.ToString());
        Assert.DoesNotContain("brand new", output.ToString() + error, StringComparison.Ordinal);
        await using PaduraksaHost running = TestHost.Create(dataDirectory: data);
        await running.StartAsync(Ct);
        Assert.Equal(LoginStatus.InvalidCredentials, (await running.Shared.Login.LoginAsync("alice", Password, Client, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await running.Shared.Login.LoginAsync("alice", "a brand new passphrase", Client, Ct)).Status);

        using StringWriter shortOut = new();
        using StringWriter shortError = new();
        Assert.Equal(ExitCodes.Failure, await IdentityCommands.ResetPasswordAsync(
            TestHost.CreateOptions(dataDirectory: data), "alice", new StringReader("short\n"), false, shortOut, shortError, Ct));
        Assert.Contains("at least", shortError.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Failure, await IdentityCommands.ResetPasswordAsync(
            TestHost.CreateOptions(dataDirectory: data), "nobody", new StringReader("a brand new passphrase\n"), false, shortOut, shortError, Ct));
        Assert.Equal(ExitCodes.Failure, await IdentityCommands.ResetPasswordAsync(
            TestHost.CreateOptions(dataDirectory: data), "alice", TextReader.Null, false, shortOut, shortError, Ct));
    }

    [Fact]
    public async Task Disable2faTurnsOffTheSecondFactorAndRemovesTheVaultSecret()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost host = TestHost.Create(dataDirectory: data))
        {
            await host.StartAsync(Ct);
            UserSummary alice = (await host.Shared.Users.CreateAsync(CommandOrigin.Cli, new("alice", null, Password, [Role.Viewer]), Ct)).User!;
            CommandOrigin origin = Client.ToOrigin(Actor.ForUser(alice.Id));
            Assert.Equal(IdentityStatus.Succeeded, (await host.Shared.TwoFactor.BeginEnrollmentAsync(origin, Password, Ct)).Status);
            // The real vault verifies real codes (tested in Paduraksa.Tests.Vault); here enrolment is left pending to keep the test to the command.
        }

        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await IdentityCommands.Disable2faAsync(TestHost.CreateOptions(dataDirectory: data), "alice", output, error, Ct);

        Assert.True(exit == ExitCodes.Success, error.ToString());
        await using PaduraksaHost running = TestHost.Create(dataDirectory: data);
        await running.StartAsync(Ct);
        Assert.False((await running.Shared.Users.ListAsync(CommandOrigin.Cli, Ct)).Users[0].TwoFactorEnabled);
        Assert.Equal(ExitCodes.Failure, await IdentityCommands.Disable2faAsync(TestHost.CreateOptions(dataDirectory: data), "nobody", output, error, Ct));
    }

    [Fact]
    public void TheCommandsAreRegistered()
    {
        Assert.Empty(PaduraksaCli.BuildRootCommand().Parse(["setup-token"]).Errors);
        Assert.Empty(PaduraksaCli.BuildRootCommand().Parse(["admin", "reset-password", "alice"]).Errors);
        Assert.Empty(PaduraksaCli.BuildRootCommand().Parse(["admin", "disable-2fa", "alice"]).Errors);
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(["admin", "reset-password"]).Errors);
        // The password is never an argument.
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(["admin", "reset-password", "alice", "--password", "x"]).Errors);
    }

    [Fact]
    public async Task InvalidAdminSettingsAreRejectedAtStartup()
    {
        Dictionary<string, string?> settings = new() { ["Paduraksa:Admin:MaxFailedAttempts"] = "1", ["Paduraksa:Admin:PasswordIterations"] = "10", ["Paduraksa:Admin:TotpIssuer"] = "a:b" };
        using StringWriter error = new();

        int exit = await IdentityCommands.SetupTokenAsync(TestHost.CreateOptions(settings: settings), TextWriter.Null, error, Ct);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("Paduraksa:Admin:MaxFailedAttempts", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Paduraksa:Admin:PasswordIterations", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Paduraksa:Admin:TotpIssuer", error.ToString(), StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}
