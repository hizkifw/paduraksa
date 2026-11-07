using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault.Crypto;

/// <summary>Where the KEK comes from: priority, validation, <c>master.key</c> creation and the checks on it (PLAN §8.3).</summary>
public sealed class KekProviderTests : IDisposable
{
    private readonly TempDirectory _data = new();

    private readonly ListLogger<KekProvider> _log = new();

    public void Dispose() => _data.Dispose();

    private string Database => _data.File("vault.db");

    private static string NewKekText()
    {
        using var kek = Kek.Generate();
        return kek.Encode();
    }

    private ResolvedKek Resolve(IReadOnlyDictionary<string, string?>? variables = null)
    {
        variables ??= new Dictionary<string, string?>();
        return new KekProvider(_log).Resolve(name => variables.GetValueOrDefault(name), _data.Path, Database);
    }

    private static byte[] IdOf(string text)
    {
        using var kek = Kek.Parse(text);
        return kek.Id;
    }

    [Fact]
    public void TheKeyFileWinsOverTheVariable()
    {
        var file = _data.File("from-file");
        var fileKey = NewKekText();
        File.WriteAllText(file, fileKey + "\n");

        using var resolved = Resolve(new Dictionary<string, string?>
        {
            [KekProvider.KeyFileVariable] = file,
            [KekProvider.KeyVariable] = NewKekText(),
        }).Kek;

        Assert.Equal(IdOf(fileKey), resolved.Id);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(KekProvider.KeyVariable, StringComparison.Ordinal));
        Assert.False(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void TheVariableWinsOverTheGeneratedFile()
    {
        var text = NewKekText();

        var resolved = Resolve(new Dictionary<string, string?> { [KekProvider.KeyVariable] = text });
        using var kek = resolved.Kek;

        Assert.Equal(KekSourceKind.Environment, resolved.Source);
        Assert.False(resolved.BesideDatabase);
        Assert.Equal(IdOf(text), kek.Id);
        Assert.False(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void WithNothingSetAKeyFileIsGeneratedAndReusedOnTheNextStart()
    {
        var first = Resolve();
        var second = Resolve();

        using (first.Kek)
        using (second.Kek)
        {
            Assert.Equal(KekSourceKind.GeneratedFile, first.Source);
            Assert.True(first.BesideDatabase);
            Assert.Equal(first.Kek.Id, second.Kek.Id);
            Assert.Equal(first.Kek.Id, IdOf(File.ReadAllText(_data.File(KekProvider.GeneratedFileName))));
        }
    }

    [Fact]
    public void EmptyVariablesAreTreatedAsUnset()
    {
        using var resolved = Resolve(new Dictionary<string, string?>
        {
            [KekProvider.KeyFileVariable] = "",
            [KekProvider.KeyVariable] = "  ",
        }).Kek;

        Assert.True(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void AnInvalidVariableIsAnErrorNotAFallThrough()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Resolve(new Dictionary<string, string?> { [KekProvider.KeyVariable] = "hunter2" }));

        Assert.Contains(KekProvider.KeyVariable, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void AMissingOrInvalidKeyFileIsAnErrorNotAFallThrough()
    {
        var missing = Assert.Throws<InvalidOperationException>(
            () => Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = _data.File("nope") }));
        Assert.Contains("cannot be read", missing.Message, StringComparison.Ordinal);

        var bad = _data.File("bad");
        File.WriteAllText(bad, "not a key");
        var invalid = Assert.Throws<InvalidOperationException>(
            () => Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = bad }));
        Assert.Contains("not usable", invalid.Message, StringComparison.Ordinal);

        Assert.False(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void AnOversizedOrNonTextKeyFileIsRefused()
    {
        var big = _data.File("big");
        File.WriteAllBytes(big, new byte[4096]);
        Assert.Throws<InvalidOperationException>(() => Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = big }));

        var binary = _data.File("binary");
        File.WriteAllBytes(binary, [.. Enumerable.Repeat((byte)0xC3, 44)]);
        Assert.Throws<InvalidOperationException>(() => Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = binary }));
    }

    [Fact]
    public void AKeyFileBesideTheDatabaseIsFlaggedAndOneElsewhereIsNot()
    {
        using var elsewhere = new TempDirectory();
        var near = _data.File("near.key");
        var far = elsewhere.File("far.key");
        var text = NewKekText();
        File.WriteAllText(near, text);
        File.WriteAllText(far, text);

        var nearResolved = Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = near });
        var farResolved = Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = far });

        using (nearResolved.Kek)
        using (farResolved.Kek)
        {
            Assert.Equal(KekSourceKind.File, nearResolved.Source);
            Assert.True(nearResolved.BesideDatabase);
            Assert.False(farResolved.BesideDatabase);
        }
    }

    [Fact]
    public void ASymlinkedKeyFileIsJudgedByItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var elsewhere = new TempDirectory();
        var target = _data.File("real.key");
        File.WriteAllText(target, NewKekText());
        var link = elsewhere.File("link.key");
        File.CreateSymbolicLink(link, target);

        var resolved = Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = link });
        using var kek = resolved.Kek;

        // The link sits elsewhere but its target is in the data directory: beside.
        Assert.True(resolved.BesideDatabase);
        Assert.True(KekProvider.IsBeside(link, _data.Path));
    }

    [Fact]
    public void ADataDirectoryReachedThroughALinkIsStillRecognised()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var elsewhere = new TempDirectory();
        var real = _data.File("real");
        Directory.CreateDirectory(real);
        var file = System.IO.Path.Combine(real, "k");
        File.WriteAllText(file, NewKekText());
        var linkedDirectory = elsewhere.File("linked");
        Directory.CreateSymbolicLink(linkedDirectory, real);

        Assert.True(KekProvider.IsBeside(System.IO.Path.Combine(linkedDirectory, "k"), real));
        Assert.True(KekProvider.IsBeside(file, linkedDirectory));
        Assert.True(KekProvider.IsBeside(file, _data.Path));
        Assert.False(KekProvider.IsBeside(file, elsewhere.Path));
    }

    [Fact]
    public void BesideMeansInsideNotJustSharingAPrefixOrClimbing()
    {
        using var other = new TempDirectory();
        var sibling = _data.Path + "-sibling";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(KekProvider.IsBeside(System.IO.Path.Combine(sibling, "k"), _data.Path));
            Assert.False(KekProvider.IsBeside(System.IO.Path.Combine(_data.Path, "..", System.IO.Path.GetFileName(sibling), "k"), _data.Path));
            Assert.True(KekProvider.IsBeside(System.IO.Path.Combine(_data.Path, "sub", "k"), _data.Path));
            Assert.True(KekProvider.IsBeside(System.IO.Path.Combine(_data.Path, "..", System.IO.Path.GetFileName(_data.Path), "k"), _data.Path));
            Assert.False(KekProvider.IsBeside(System.IO.Path.Combine(other.Path, "k"), _data.Path));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AnEmptyGeneratedKeyFileSaysItCanBeDeleted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        var path = _data.File(KekProvider.GeneratedFileName);
        File.WriteAllBytes(path, []);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve());
        Assert.Contains("delete it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ANewKeyFileIsPrivateAndHoldsOnlyTheKey()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var resolved = Resolve().Kek;

        var path = _data.File(KekProvider.GeneratedFileName);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(resolved.Encode() + "\n", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_data.Path));
    }

    [Fact]
    public void AnExistingKeyFileIsNeverOverwritten()
    {
        using var existing = Kek.Generate();
        var path = _data.File(KekProvider.GeneratedFileName);
        File.WriteAllText(path, existing.Encode() + "\n");
        using var other = Kek.Generate();

        Assert.False(KekProvider.TryCreateKeyFile(path, other));

        Assert.Equal(existing.Encode() + "\n", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_data.Path));
    }

    [Fact]
    public void ConcurrentFirstStartsAgreeOnOneKey()
    {
        var results = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => Resolve())
            .ToList();

        try
        {
            Assert.Single(results.Select(r => Convert.ToHexString(r.Kek.Id)).Distinct(StringComparer.Ordinal));
            Assert.Equal([_data.File(KekProvider.GeneratedFileName)], Directory.GetFiles(_data.Path));
        }
        finally
        {
            results.ForEach(r => r.Kek.Dispose());
        }
    }

    [Fact]
    public void ADatabaseWithDataAndNoKeyDoesNotGetANewKey()
    {
        File.WriteAllBytes(Database, [1, 2, 3]);

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve());

        Assert.Contains("no master key", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    public void AnEmptyDatabaseFileDoesNotStopTheKeyBeingGenerated()
    {
        File.WriteAllBytes(Database, []);

        using var resolved = Resolve().Kek;

        Assert.True(File.Exists(_data.File(KekProvider.GeneratedFileName)));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AGeneratedKeyFileOthersCanReadIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        using (Resolve().Kek)
        {
        }

        var path = _data.File(KekProvider.GeneratedFileName);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve());

        Assert.Contains("chmod 600", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AGeneratedKeyFileTheGroupCanReadOnlyWarns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        using (Resolve().Kek)
        {
        }

        File.SetUnixFileMode(_data.File(KekProvider.GeneratedFileName), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        using var resolved = Resolve().Kek;

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("group", StringComparison.Ordinal));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AnExplicitKeyFileMountedWorldReadableIsUsedWithAWarning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        var file = _data.File("secret-mount");
        File.WriteAllText(file, NewKekText());
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using var resolved = Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = file }).Kek;

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("every user", StringComparison.Ordinal));
    }

    [Fact]
    public void NeitherTheKeyNorItsTextIsEverLogged()
    {
        var text = NewKekText();
        File.WriteAllText(_data.File("k"), text);

        using (Resolve(new Dictionary<string, string?> { [KekProvider.KeyFileVariable] = _data.File("k"), [KekProvider.KeyVariable] = text }).Kek)
        using (Resolve().Kek)
        {
        }

        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains(text, StringComparison.Ordinal));
    }
}
