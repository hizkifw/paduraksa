using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>First-run setup: incomplete until an Owner exists, gated by a one-time token in an owner-only file.</summary>
public sealed class SetupServiceTests
{
    private static string TokenFile(IdentityHarness h) => Path.Combine(h.DataDirectory, SetupService.TokenFileName);

    [Fact]
    public async Task SetupIsIncompleteUntilAnOwnerExists()
    {
        await using var h = await CreateAsync();
        Assert.False(await h.Setup.IsCompleteAsync(Ct));
        Assert.False((await h.Setup.GetStatusAsync(Ct)).Complete);

        await h.CreateUserAsync("viewer"); // not an Owner
        Assert.False(await h.Setup.IsCompleteAsync(Ct));

        await h.CreateUserAsync("owner", Role.Owner);
        Assert.True((await h.Setup.GetStatusAsync(Ct)).Complete);
    }

    [Fact]
    public async Task TheTokenIsCreatedOnceWithHighEntropyAndOwnerOnlyAccess()
    {
        await using var h = await CreateAsync();

        var token = await h.Setup.EnsureTokenAsync(Ct);
        var again = await h.Setup.EnsureTokenAsync(Ct);

        Assert.Equal(token, again);
        Assert.Equal(43, token!.Length); // 32 random bytes, base64url
        Assert.Equal(token, (await File.ReadAllTextAsync(TokenFile(h), Ct)).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenFile(h)));
        }

        File.Delete(TokenFile(h));
        Assert.NotEqual(token, await h.Setup.EnsureTokenAsync(Ct));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ATokenFileOthersCanReadIsNotTrustedAndIsReplaced()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        await using var h = await CreateAsync();
        var token = await h.Setup.EnsureTokenAsync(Ct);
        File.SetUnixFileMode(TokenFile(h), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var replaced = await h.Setup.EnsureTokenAsync(Ct);

        Assert.NotEqual(token, replaced);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenFile(h)));
    }

    [Fact]
    public async Task CompletingSetupCreatesTheOwnerAtomicallyAndConsumesTheToken()
    {
        await using var h = await CreateAsync();
        var token = (await h.Setup.EnsureTokenAsync(Ct))!;

        var result = await h.Setup.CompleteSetupAsync(token, "Root", Password, "The Admin", h.Client, Ct);

        Assert.Equal(SetupOutcome.Succeeded, result.Outcome);
        Assert.Equal(("Root", "The Admin"), (result.Owner!.Username, result.Owner.DisplayName));
        Assert.Equal([Role.Owner], result.Owner.Roles);
        Assert.False(File.Exists(TokenFile(h)));
        Assert.True(await h.Setup.IsCompleteAsync(Ct));
        Assert.Null(await h.Setup.EnsureTokenAsync(Ct));
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("root", Password, h.Client, Ct)).Status);
        var audit = Assert.Single(await h.AuditAsync("setup.complete"));
        Assert.Equal((AuditOutcome.Success, result.Owner.Id.ToString()), (audit.Outcome, audit.Target));
        Assert.DoesNotContain(token, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondSetupIsRefusedWhateverIsPresented()
    {
        await using var h = await CreateAsync();
        var token = (await h.Setup.EnsureTokenAsync(Ct))!;
        await h.Setup.CompleteSetupAsync(token, "root", Password, null, h.Client, Ct);

        var again = await h.Setup.CompleteSetupAsync(token, "other", Password, null, h.Client, Ct);
        var guess = await h.Setup.CompleteSetupAsync("anything", "other", Password, null, h.Client, Ct);

        Assert.Equal(SetupOutcome.AlreadyComplete, again.Outcome);
        Assert.Equal(SetupOutcome.AlreadyComplete, guess.Outcome);
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM Users"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not the token")]
    public async Task AWrongTokenCreatesNothingAndIsAudited(string presented)
    {
        await using var h = await CreateAsync();
        var token = await h.Setup.EnsureTokenAsync(Ct);

        var result = await h.Setup.CompleteSetupAsync(presented, "root", Password, null, h.Client, Ct);

        Assert.Equal(SetupOutcome.InvalidToken, result.Outcome);
        Assert.Equal(0L, await h.Db.ScalarAsync("SELECT count(*) FROM Users"));
        Assert.True(File.Exists(TokenFile(h)));
        var audit = Assert.Single(await h.AuditAsync("setup.complete"));
        Assert.Equal((ActorKind.System, AuditOutcome.Denied), (audit.ActorKind, audit.Outcome));
        Assert.DoesNotContain(token!, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATokenFileNobodyCanCompleteSetup()
    {
        await using var h = await CreateAsync();

        Assert.Equal(SetupOutcome.InvalidToken, (await h.Setup.CompleteSetupAsync("whatever", "root", Password, null, h.Client, Ct)).Outcome);
    }

    [Fact]
    public async Task TheOwnerNeedsAValidNameAndPasswordAndTheTokenSurvivesARefusal()
    {
        await using var h = await CreateAsync();
        var token = (await h.Setup.EnsureTokenAsync(Ct))!;

        var weak = await h.Setup.CompleteSetupAsync(token, "root", "short", null, h.Client, Ct);
        var badName = await h.Setup.CompleteSetupAsync(token, "no spaces", Password, null, h.Client, Ct);

        Assert.Equal(SetupOutcome.Invalid, weak.Outcome);
        Assert.Contains("at least 12", weak.Problem, StringComparison.Ordinal);
        Assert.Equal(SetupOutcome.Invalid, badName.Outcome);
        Assert.True(File.Exists(TokenFile(h)));
        Assert.Equal(SetupOutcome.Succeeded, (await h.Setup.CompleteSetupAsync(token, "root", Password, null, h.Client, Ct)).Outcome);
    }

    [Fact]
    public async Task SimultaneousAttemptsMakeExactlyOneOwner()
    {
        await using var h = await CreateAsync();
        var token = (await h.Setup.EnsureTokenAsync(Ct))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(async () => (await h.Setup.CompleteSetupAsync(token, $"root{i}", Password, null, h.Client, Ct)).Outcome, Ct)));

        Assert.Equal(1, results.Count(r => r == SetupOutcome.Succeeded));
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM RoleAssignments WHERE Role = 'owner'"));
    }

    [Fact]
    public async Task AnIncompleteSetupPrintsTheBannerWholeToTheConsole()
    {
        await using var h = await CreateAsync();
        using StringWriter console = new();

        await h.Setup.AnnounceAsync(console, Ct);

        var token = (await File.ReadAllTextAsync(TokenFile(h), Ct)).Trim();
        var printed = console.ToString();
        Assert.Contains("Paduraksa first-run setup", printed, StringComparison.Ordinal);
        Assert.Contains(token, printed, StringComparison.Ordinal);
        // The box keeps its line breaks: one line per row, not one flattened line.
        Assert.True(printed.Split('\n').Length > 5);
    }

    [Fact]
    public async Task AStaleTokenFileIsRemovedOnceSetupIsComplete()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("owner", Role.Owner);
        await File.WriteAllTextAsync(TokenFile(h), "leftover", Ct);

        await h.Setup.AnnounceAsync(console: null, Ct);

        Assert.False(File.Exists(TokenFile(h)));
        Assert.Equal(IdentityStatus.Succeeded, (await h.Users.GetAsync(CommandOrigin.Cli, (await h.Users.ListAsync(CommandOrigin.Cli, Ct)).Users[0].Id, Ct)).Status);
    }
}
