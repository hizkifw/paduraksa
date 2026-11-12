using Paduraksa.Core.Hosting;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class DataDirectoryLockTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("paduraksa-lock-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AHeldClaimCanNotBeTakenAgainUntilItIsReleased()
    {
        using var first = DataDirectoryLock.Acquire(_root);

        Assert.Null(DataDirectoryLock.TryAcquire(_root));
        var ex = Assert.Throws<DataDirectoryInUseException>(() => DataDirectoryLock.Acquire(_root));
        Assert.Contains(_root, ex.Message, StringComparison.Ordinal);

        first.Dispose();
        using var again = DataDirectoryLock.TryAcquire(_root);
        Assert.NotNull(again);
    }

    [Fact]
    public void OnAFileSystemThatLocksTheClaimSaysItIsEnforced()
    {
        // The unenforced case (a network mount where flock fails with ENOLCK or EOPNOTSUPP) can't be made on a test machine's local disk.
        using var claim = DataDirectoryLock.Acquire(_root);

        Assert.True(claim.Enforced);
    }

    [Fact]
    public void ADifferentDirectoryIsUnaffected()
    {
        var other = Path.Combine(_root, "other");
        using var first = DataDirectoryLock.Acquire(_root);

        using var second = DataDirectoryLock.TryAcquire(other);

        Assert.NotNull(second);
    }

    [Fact]
    public void TheLockFileStaysAfterReleaseAndIsPrivate()
    {
        var path = Path.Combine(_root, "run", DataDirectoryLock.FileName);
        DataDirectoryLock.Acquire(_root).Dispose();

        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(_root, "run")));
        }
    }
}
