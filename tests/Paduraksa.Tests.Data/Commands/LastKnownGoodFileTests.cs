using System.Security.Cryptography;
using System.Text;
using Paduraksa.Application.Config;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Commands.Objects;

namespace Paduraksa.Tests.Data.Commands;

public sealed class LastKnownGoodFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-lastgood-").FullName;

    private string Target => Path.Combine(_directory, LastKnownGoodFile.FileName);

    [Fact]
    public async Task WritesTheEntryAndReadsItBack()
    {
        await using var file = new LastKnownGoodFile(Target);
        var entry = Entry(1, "a.example.com");

        file.Offer(entry);
        await file.FlushAsync();

        Assert.True(file.TryRead(out var read, out var problem), problem);
        Assert.Equal(entry.Revision, read.Revision);
        Assert.Equal(entry.Document.ToArray(), read.Document.ToArray());
        Assert.Equal(entry.DocumentHash.ToArray(), read.DocumentHash.ToArray());
        Assert.Equal(["a.example.com"], read.Parsed!.Hosts.Single().Names);
        Assert.Empty(Directory.GetFileSystemEntries(_directory, file.TempPattern));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Target));
        }
    }

    [Fact]
    public async Task KeepsTheNewestOfferAndIgnoresOlderOnes()
    {
        await using var file = new LastKnownGoodFile(Target);

        file.Offer(Entry(2, "b.example.com"));
        file.Offer(Entry(1, "a.example.com"));
        await file.FlushAsync();
        file.Offer(Entry(2, "c.example.com"));
        await file.FlushAsync();

        Assert.True(file.TryRead(out var read, out var problem), problem);
        Assert.Equal(2, read.Revision.Seq);
        Assert.Equal(["b.example.com"], read.Parsed!.Hosts.Single().Names);
    }

    [Fact]
    public async Task ReplacesASymbolicLinkAtTheTargetInsteadOfWritingThroughIt()
    {
        var victim = Path.Combine(_directory, "victim.txt");
        await File.WriteAllTextAsync(victim, "untouched", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Target, victim);
        await using var file = new LastKnownGoodFile(Target);

        // Reading refuses the link ...
        Assert.False(file.TryRead(out _, out var problem));
        Assert.Contains("symbolic link", problem, StringComparison.Ordinal);

        // ... and writing replaces it with a regular file.
        file.Offer(Entry(1, "a.example.com"));
        await file.FlushAsync();

        Assert.Equal("untouched", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.Null(new FileInfo(Target).LinkTarget);
        Assert.True(file.TryRead(out _, out problem), problem);
    }

    [Fact]
    public async Task RemovesLeftoverTempFilesWithoutWritingThroughLinks()
    {
        await using var file = new LastKnownGoodFile(Target);
        var victim = Path.Combine(_directory, "victim.txt");
        await File.WriteAllTextAsync(victim, "untouched", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Target + ".planted.tmp", victim);
        await File.WriteAllTextAsync(Target + ".crashed.tmp", "{ half a wri", TestContext.Current.CancellationToken);

        file.Offer(Entry(1, "a.example.com"));
        await file.FlushAsync();

        Assert.Equal("untouched", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.True(file.TryRead(out _, out var problem), problem);
        Assert.Empty(Directory.GetFileSystemEntries(_directory, file.TempPattern));
    }

    [Fact]
    public async Task AFailedWriteKeepsThePreviousFile()
    {
        await using var file = new LastKnownGoodFile(Target);
        file.Offer(Entry(1, "a.example.com"));
        await file.FlushAsync();

        // A directory named like a temp file can't be removed as one: the write fails (logged, the writer keeps
        // going), the old file stays, and once it's gone the next write succeeds.
        var blocker = Target + ".blocker.tmp";
        Directory.CreateDirectory(Path.Combine(blocker, "inside"));
        file.Offer(Entry(2, "b.example.com"));
        await file.FlushAsync();
        Assert.True(file.TryRead(out var read, out var problem), problem);
        Assert.Equal(1, read.Revision.Seq);

        Directory.Delete(blocker, recursive: true);
        file.Offer(Entry(3, "c.example.com"));
        await file.FlushAsync();
        Assert.True(file.TryRead(out read, out problem), problem);
        Assert.Equal(3, read.Revision.Seq);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered document")]
    [InlineData("wrong format")]
    [InlineData("not json")]
    [InlineData("bad revision")]
    [InlineData("seq as a string")]
    [InlineData("not an object")]
    public async Task RefusesAFileThatCantBeTrusted(string damage)
    {
        await using var file = new LastKnownGoodFile(Target);
        var text = Encoding.UTF8.GetString(LastKnownGoodFile.Render(Entry(1, "a.example.com")));
        switch (damage)
        {
            case "tampered document":
                text = text.Replace("a.example.com", "b.example.com", StringComparison.Ordinal);
                break;
            case "wrong format":
                text = text.Replace(LastKnownGoodFile.Format, "paduraksa.dev/last-good/v0", StringComparison.Ordinal);
                break;
            case "not json":
                text = text[..^5];
                break;
            case "bad revision":
                text = text.Replace("\"revision\": \"rev_", "\"revision\": \"host_", StringComparison.Ordinal);
                break;
            case "seq as a string":
                text = text.Replace("\"seq\": 1", "\"seq\": \"1\"", StringComparison.Ordinal);
                break;
            case "not an object":
                text = "[1, 2, 3]";
                break;
        }

        if (damage != "missing")
        {
            await File.WriteAllTextAsync(Target, text, TestContext.Current.CancellationToken);
        }

        Assert.False(file.TryRead(out var read, out var problem));
        Assert.Null(read);
        Assert.Contains(Target, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresOffersOnceDisposed()
    {
        var file = new LastKnownGoodFile(Target);
        await file.DisposeAsync();

        file.Offer(Entry(1, "a.example.com"));
        await file.FlushAsync();

        Assert.False(File.Exists(Target));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static LastKnownGoodEntry Entry(long seq, string name)
    {
        var document = CanonicalConfigJson.Serialize(new ConfigDocument(Hosts: [RespondHost(HostId.New(), name)]));
        return new LastKnownGoodEntry(new RevisionRef(ConfigRevisionId.New(), seq), document, SHA256.HashData(document));
    }
}
