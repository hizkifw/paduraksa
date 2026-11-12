using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Backup;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Data.Identity;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Backup;

/// <summary>
/// The backup use case over the real identity services and config database: who may make one, what is audited, and what a refusal costs.
/// The archive itself (encryption, restore) is tested in <c>Paduraksa.Tests.Vault</c> and through the CLI; a fake vault stands in here.
/// </summary>
public sealed class BackupServiceTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";

    private IdentityHarness _h = null!;

    private ServiceProvider _services = null!;

    private readonly FakeVaultBackup _vault = new();

    private string _data = null!;

    private int _users;

    private BackupService Service => _services.GetRequiredService<BackupService>();

    public async ValueTask InitializeAsync()
    {
        _h = await CreateAsync();
        _data = Directory.CreateTempSubdirectory("paduraksa-backup-service-").FullName;
        _services = new ServiceCollection()
            .AddSingleton(_h.Db.Factory)
            .AddSingleton<TimeProvider>(_h.Time)
            .AddPaduraksaIdentity(_data, Fast())
            .AddSingleton<IVaultBackup>(_vault)
            .AddPaduraksaBackup(_data)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _h.DisposeAsync();
        Directory.Delete(_data, recursive: true);
    }

    private async Task<CommandOrigin> OriginOfAsync(Role role)
    {
        var user = await _h.CreateUserAsync("user-" + role.ToName(), role);
        return _h.As(user);
    }

    /// <summary>The origin of a token owned by a user of <paramref name="role"/> and scoped to <paramref name="scopes"/>; the owner needs TokensOwn, which every role has.</summary>
    private async Task<CommandOrigin> TokenOriginAsync(Role role, params Permission[] scopes)
    {
        var user = await _h.CreateUserAsync("token-owner-" + _users++.ToString(System.Globalization.CultureInfo.InvariantCulture), role);
        var issued = await _h.Tokens.IssueAsync(_h.As(user), new IssueApiTokenRequest("backup", [.. scopes]), Ct);
        Assert.True(issued.Succeeded, issued.Problem);
        return new CommandOrigin(Actor.ForToken(issued.Issued!.Summary.Id), Channel.Api, _h.Client.ClientIp);
    }

    [Fact]
    public async Task TheOwnerMakesABackupAndItIsAuditedWithItsSizeAndNeverThePassphrase()
    {
        var origin = await OriginOfAsync(Role.Owner);
        using MemoryStream destination = new();

        var result = await Service.CreateAsync(origin, Passphrase, destination, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(destination.Length, result.Bytes);
        Assert.Equal(Passphrase, _vault.LastPassphrase);
        var audit = Assert.Single(await _h.AuditAsync("backup.create"));
        Assert.Equal((ActorKind.User, AuditOutcome.Success), (audit.ActorKind, audit.Outcome));
        Assert.Equal($"{{\"bytes\":{result.Bytes}}}", audit.Details);
        Assert.All(await _h.AuditAsync(), e => Assert.DoesNotContain(Passphrase, e.Details ?? string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Auditor)]
    public async Task OnlyTheOwnerMayAndAnyoneElseIsRefusedAndAuditedWithoutTheVaultBeingTouched(Role role)
    {
        var origin = await OriginOfAsync(role);
        using MemoryStream destination = new();

        var result = await Service.CreateAsync(origin, Passphrase, destination, Ct);

        Assert.Equal(BackupStatus.Denied, result.Status);
        Assert.Equal(0, _vault.Calls);
        Assert.Equal(0, destination.Length);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(await _h.AuditAsync("backup.create")).Outcome);
    }

    [Fact]
    public async Task ATokenCarriesBackupCreateAsAScopeAndOnlyThenMayMakeOne()
    {
        var withScope = await TokenOriginAsync(Role.Owner, Permission.BackupCreate);
        var withoutScope = await TokenOriginAsync(Role.Owner, Permission.ConfigRead);

        Assert.True((await Service.CreateAsync(withScope, Passphrase, new MemoryStream(), Ct)).Succeeded);
        Assert.Equal(BackupStatus.Denied, (await Service.CreateAsync(withoutScope, Passphrase, new MemoryStream(), Ct)).Status);
        Assert.Equal(1, _vault.Calls);
    }

    [Fact]
    public async Task AUserWithoutThePermissionCannotMintATokenWithTheScope()
    {
        var admin = await _h.CreateUserAsync("some-admin", Role.Admin);

        var issued = await _h.Tokens.IssueAsync(_h.As(admin), new IssueApiTokenRequest("backup", [Permission.BackupCreate]), Ct);

        Assert.False(issued.Succeeded);
    }

    [Fact]
    public async Task TheCliAndTheSystemMayBecauseTheyAreTheMachineItself()
    {
        Assert.True((await Service.CreateAsync(CommandOrigin.Cli, Passphrase, new MemoryStream(), Ct)).Succeeded);
        Assert.Equal(ActorKind.Cli, Assert.Single(await _h.AuditAsync("backup.create")).ActorKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eleven char")]
    public async Task AWeakPassphraseIsRefusedBeforeTheVaultIsAskedAndIsAudited(string? passphrase)
    {
        var origin = await OriginOfAsync(Role.Owner);

        var result = await Service.CreateAsync(origin, passphrase, new MemoryStream(), Ct);

        Assert.Equal(BackupStatus.Invalid, result.Status);
        Assert.NotNull(result.Problem);
        Assert.Equal(0, _vault.Calls);
        Assert.Equal(AuditOutcome.Invalid, Assert.Single(await _h.AuditAsync("backup.create")).Outcome);
    }

    [Fact]
    public async Task AFailureInTheVaultIsAFailedBackupWithAGenericProblemAndTheDetailForTheCli()
    {
        var origin = await OriginOfAsync(Role.Owner);
        _vault.Throw = new InvalidOperationException("The master key is not usable.");

        var result = await Service.CreateAsync(origin, Passphrase, new MemoryStream(), Ct);

        Assert.Equal(BackupStatus.Failed, result.Status);
        Assert.DoesNotContain("master key", result.Problem, StringComparison.Ordinal);
        Assert.Equal("The master key is not usable.", result.Detail);
        Assert.Equal(AuditOutcome.Failed, Assert.Single(await _h.AuditAsync("backup.create")).Outcome);
    }

    [Fact]
    public async Task ABackupThatIsAlreadyRunningIsBusyAndNotAudited()
    {
        var origin = await OriginOfAsync(Role.Owner);
        _vault.Throw = new BackupException(BackupFailure.Busy, "A backup is already being made.");

        var result = await Service.CreateAsync(origin, Passphrase, new MemoryStream(), Ct);

        Assert.Equal(BackupStatus.Busy, result.Status);
        Assert.Empty(await _h.AuditAsync("backup.create"));
    }

    [Fact]
    public async Task ADownloadIsAPrivateFileThatIsGoneWhenItIsDisposed()
    {
        var origin = await OriginOfAsync(Role.Owner);

        var result = await Service.CreateDownloadAsync(origin, Passphrase, Ct);

        Assert.True(result.Result.Succeeded);
        var download = result.Download!;
        Assert.Matches(@"^paduraksa-backup-\d{8}-\d{6}\.pdkbak$", download.FileName);
        Assert.Equal(0, download.Content.Position);
        Assert.Equal(_vault.Archive.Length, download.Length);
        using (MemoryStream copy = new())
        {
            await download.Content.CopyToAsync(copy, Ct);
            Assert.Equal(_vault.Archive, copy.ToArray());
        }

        var files = Directory.GetFiles(Path.Combine(_data, "run"));
        var file = Assert.Single(files);
        if (!OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416 // Skipped on Windows.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
#pragma warning restore CA1416
        }

        await download.DisposeAsync();
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ARefusedOrFailedDownloadLeavesNoFile()
    {
        var owner = await OriginOfAsync(Role.Owner);
        var viewer = await OriginOfAsync(Role.Viewer);

        Assert.Null((await Service.CreateDownloadAsync(viewer, Passphrase, Ct)).Download);
        Assert.Null((await Service.CreateDownloadAsync(owner, "short", Ct)).Download);
        _vault.Throw = new InvalidOperationException("no");
        Assert.Null((await Service.CreateDownloadAsync(owner, Passphrase, Ct)).Download);

        Assert.Empty(Directory.Exists(Path.Combine(_data, "run")) ? Directory.GetFiles(Path.Combine(_data, "run")) : []);
    }

    [Fact]
    public async Task ADeniedDownloadIsAuditedOnce()
    {
        var viewer = await OriginOfAsync(Role.Viewer);

        var result = await Service.CreateDownloadAsync(viewer, Passphrase, Ct);

        Assert.Equal(BackupStatus.Denied, result.Result.Status);
        Assert.Single(await _h.AuditAsync("backup.create"));
    }

    /// <summary>A vault that writes a fixed archive and remembers what it was asked.</summary>
    private sealed class FakeVaultBackup : IVaultBackup
    {
        public byte[] Archive { get; } = Encoding.ASCII.GetBytes("PDKBAK-test-archive-bytes");

        public int Calls { get; private set; }

        public string? LastPassphrase { get; private set; }

        public Exception? Throw { get; set; }

        public ValueTask<BackupManifest> CreateAsync(BackupSources sources, ReadOnlyMemory<char> passphrase, Stream destination, CancellationToken cancellationToken)
        {
            Calls++;
            LastPassphrase = passphrase.ToString();
            if (Throw is not null)
            {
                throw Throw;
            }

            destination.Write(Archive);
            return ValueTask.FromResult(new BackupManifest(1, "test", DateTimeOffset.UnixEpoch, [], [], [], new BackupKdf("argon2id", 19, 1, 1, 1)));
        }

        public ValueTask<IStagedBackup> OpenAsync(Stream archive, ReadOnlyMemory<char> passphrase, string stagingDirectory, IReadOnlyCollection<string> knownConfigMigrations, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
