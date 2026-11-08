using System.Net;
using System.Net.Sockets;

using Paduraksa.Core.Hosting;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>The app's internal listener (PLAN §3.1): socket selection, permissions, the hop secret and the fallback.</summary>
public sealed class AdminInternalListenerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("paduraksa-admin-internal-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void DisabledMeansNoListener()
    {
        using AdminInternalListener listener = new(_root, enabled: false);

        Assert.False(listener.Enabled);
        Assert.False(listener.UsesUnixSocket);
        Assert.Null(listener.SocketPath);
        Assert.Null(listener.LoopbackEndPoint);
        Assert.True(listener.IsValidSecret(listener.Secret));
    }

    [Fact]
    public void TheUnixSocketSitsInAPrivateRunDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix-domain sockets are not used on Windows.");
        }

        using AdminInternalListener listener = new(_root, enabled: true);

        Assert.True(listener.Enabled);
        Assert.True(listener.UsesUnixSocket);
        string socket = listener.SocketPath!;
        Assert.Equal(Path.Combine(_root, "run", AdminInternalListener.SocketFileName), socket);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(socket)!));
        }

        Assert.True(listener.IsValidSecret(listener.Secret));
        Assert.False(listener.IsValidSecret(listener.Secret + "0"));
        Assert.False(listener.IsValidSecret(""));
    }

    [Fact]
    public void APathTooLongForASocketFallsBackToLoopback()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix-domain sockets are not used on Windows.");
        }

        string deep = Path.Combine(_root, new string('d', 120));
        using AdminInternalListener listener = new(deep, enabled: true);

        Assert.True(listener.Enabled);
        Assert.False(listener.UsesUnixSocket);
        Assert.Null(listener.SocketPath);
        Assert.NotNull(listener.FallbackReason);

        listener.BindLoopback(43210);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 43210), listener.LoopbackEndPoint);
    }

    [Fact]
    public void AStaleSocketIsRemovedBeforeBinding()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix-domain sockets are not used on Windows.");
        }

        string run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run);
        string socketPath = Path.Combine(run, AdminInternalListener.SocketFileName);
        using (Socket stale = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(socketPath));
            stale.Listen(1);
        }

        using AdminInternalListener listener = new(_root, enabled: true);

        Assert.True(listener.UsesUnixSocket);
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public void DisposeDoesNotDeleteAFileAnotherProcessPutAtThePath()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions and statx differ on Windows.");
        }

        using AdminInternalListener listener = new(_root, enabled: true);
        string socketPath = listener.SocketPath!;
        File.WriteAllText(socketPath, "this process");
        listener.RecordBoundSocket();

        // A different file, not a rewrite of ours. Deleting ours frees its inode, so the new file may get the same inode
        // number straight away (tmpfs and overlayfs do); the listener must still know it is not the socket it bound.
        File.Delete(socketPath);
        File.WriteAllText(socketPath, "someone else");

        listener.Dispose();

        Assert.True(File.Exists(socketPath));
        Assert.Equal("someone else", File.ReadAllText(socketPath));
    }

    [Fact]
    public void DisposeRemovesTheSocketItBound()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions and statx differ on Windows.");
        }

        using AdminInternalListener listener = new(_root, enabled: true);
        string socketPath = listener.SocketPath!;
        File.WriteAllText(socketPath, "this process");
        listener.RecordBoundSocket();

        listener.Dispose();

        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public void ALiveSocketFailsStartupInsteadOfBeingDeleted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix-domain sockets are not used on Windows.");
        }

        string run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run);
        string socketPath = Path.Combine(run, AdminInternalListener.SocketFileName);
        using Socket live = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        live.Bind(new UnixDomainSocketEndPoint(socketPath));
        live.Listen(1);

        AdminInternalStartupException ex = Assert.Throws<AdminInternalStartupException>(() => new AdminInternalListener(_root, enabled: true));
        Assert.Contains("already served", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(socketPath));
    }

    [Fact]
    public void ASymlinkedRunDirectoryIsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions and symlinks differ on Windows.");
        }

        string target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        string run = Path.Combine(_root, "run");
        Directory.CreateSymbolicLink(run, target);

        AdminInternalStartupException ex = Assert.Throws<AdminInternalStartupException>(() => new AdminInternalListener(_root, enabled: true));
        Assert.Contains("symlink", ex.Message, StringComparison.Ordinal);
    }
}
