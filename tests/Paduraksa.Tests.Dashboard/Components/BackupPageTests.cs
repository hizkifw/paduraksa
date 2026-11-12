using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Backup;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Backup;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Settings > Backup: the passphrase twice with its hint, the download, what a refusal says, and that the passphrase is never rendered back.</summary>
public sealed class BackupPageTests
{
    private const string Passphrase = "correct horse battery staple";

    private static (FakeBackups Backups, IRenderedComponent<Backup> Page, DashboardTestContext Context) Open(Role role = Role.Owner)
    {
        DashboardTestContext context = new();
        FakeBackups backups = new();
        context.Services.AddSingleton<IDashboardBackup>(backups);
        IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        return (backups, context.Render<Backup>(), context);
    }

    [Fact]
    public async Task TheRestoreCardNamesTheCommandAndSaysItCannotRunWhileTheServerDoes()
    {
        var (_, page, context) = Open();
        await using (context)
        {
            Assert.Contains("paduraksa restore", page.Find("[data-command=restore]").TextContent, StringComparison.Ordinal);
            Assert.Contains("can't run while the server does", page.Find("[data-card=restore]").TextContent, StringComparison.Ordinal);
            Assert.Contains("pre-restore-", page.Find("[data-card=restore]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Nothing can recover a lost one", page.Find("[data-notice=passphrase]").TextContent, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("short", 1)]
    [InlineData("twelve chars", 2)]
    [InlineData("correct horse battery staple", 4)]
    public async Task TheHintFollowsTheLengthAndSaysHowManyCharactersAreMissing(string passphrase, int level)
    {
        var (_, page, context) = Open();
        await using (context)
        {
            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = passphrase });

            if (level == 0)
            {
                Assert.Empty(page.FindAll("[data-strength]"));
                return;
            }

            Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), page.Find("[data-strength]").GetAttribute("data-strength"));
            if (passphrase == "short")
            {
                Assert.Contains("7 more characters", page.Find("[data-strength]").TextContent, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AWeakOrMismatchedPassphraseIsNotSentAndSaysWhy()
    {
        var (backups, page, context) = Open();
        await using (context)
        {
            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = "short" });
            await page.Find("input[data-field=repeat]").InputAsync(new() { Value = "short" });
            await page.Find("form").SubmitAsync();
            Assert.Contains("at least 12 characters", page.Markup, StringComparison.Ordinal);

            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = Passphrase });
            await page.Find("input[data-field=repeat]").InputAsync(new() { Value = Passphrase + "x" });
            await page.Find("form").SubmitAsync();
            Assert.Contains("The two passphrases differ", page.Markup, StringComparison.Ordinal);

            Assert.Empty(backups.Requests);
        }
    }

    [Fact]
    public async Task ADownloadHandsTheBytesToTheBrowserAndForgetsThePassphrase()
    {
        var (backups, page, context) = Open();
        await using (context)
        {
            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = Passphrase });
            await page.Find("input[data-field=repeat]").InputAsync(new() { Value = Passphrase });

            await page.Find("form").SubmitAsync();

            Assert.Equal([Passphrase], backups.Requests);
            var invocation = Assert.Single(context.JSInterop.Invocations["paduraksa.download"]);
            Assert.Equal("paduraksa-backup-test.pdkbak", invocation.Arguments[0]);
            Assert.Equal("application/octet-stream", invocation.Arguments[1]);
            Assert.True(backups.Disposed);
            Assert.DoesNotContain(Passphrase, page.Markup, StringComparison.Ordinal);
            Assert.Equal(string.Empty, page.Find("input[data-field=passphrase]").GetAttribute("value") ?? string.Empty);
            Assert.Empty(page.FindAll("[data-result=refused]"));
        }
    }

    [Theory]
    [InlineData(BackupStatus.Denied, "Only the Owner may make a backup.")]
    [InlineData(BackupStatus.Busy, "A backup is already being made.")]
    [InlineData(BackupStatus.Failed, "The backup could not be made; the server's log says why.")]
    public async Task ARefusalIsShownAndNothingIsDownloaded(BackupStatus status, string problem)
    {
        var (backups, page, context) = Open();
        await using (context)
        {
            backups.Next = new BackupDownloadResult(new BackupResult(status, problem));
            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = Passphrase });
            await page.Find("input[data-field=repeat]").InputAsync(new() { Value = Passphrase });

            await page.Find("form").SubmitAsync();

            Assert.Contains(problem, page.Find("[data-result=refused]").TextContent, StringComparison.Ordinal);
            Assert.Empty(context.JSInterop.Invocations["paduraksa.download"]);
            Assert.DoesNotContain(Passphrase, page.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnExceptionIsAnErrorNotACrashAndTheMessageIsNotShown()
    {
        var (backups, page, context) = Open();
        await using (context)
        {
            backups.Throw = new InvalidOperationException("secret-detail");
            await page.Find("input[data-field=passphrase]").InputAsync(new() { Value = Passphrase });
            await page.Find("input[data-field=repeat]").InputAsync(new() { Value = Passphrase });

            await page.Find("form").SubmitAsync();

            Assert.Contains("server's log says why", page.Find("[data-result=refused]").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-detail", page.Markup, StringComparison.Ordinal);
        }
    }

    private sealed class FakeBackups : IDashboardBackup, IDisposable
    {
        public List<string> Requests { get; } = [];

        public BackupDownloadResult? Next { get; set; }

        public Exception? Throw { get; set; }

        private FileStream? _stream;

        /// <summary>Whether the page disposed the download it was given (the file is deleted with it).</summary>
        public bool Disposed => _stream is { CanRead: false };

        public void Dispose() => _stream?.Dispose();

        public Task<BackupDownloadResult> CreateAsync(CommandOrigin origin, string passphrase, CancellationToken cancellationToken = default)
        {
            Requests.Add(passphrase);
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Next is { } next)
            {
                return Task.FromResult(next);
            }

            var path = Path.Combine(Path.GetTempPath(), "paduraksa-test-" + Path.GetRandomFileName());
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            _stream.Write("PDKBAK-bytes"u8);
            _stream.Position = 0;
            return Task.FromResult(new BackupDownloadResult(new BackupResult(BackupStatus.Created, Bytes: _stream.Length), new BackupDownload(_stream, "paduraksa-backup-test.pdkbak")));
        }
    }
}
