using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Paduraksa.Core.Hosting;

/// <summary>
/// The admin app's internal listener (PLAN §3.1): the public proxy reaches the admin through it without a second public port.
/// It is a Unix domain socket at <c>&lt;data dir&gt;/run/admin.sock</c> where one can be used, and a loopback TCP listener on an
/// OS-assigned port otherwise.
/// </summary>
/// <remarks>
/// <para><b>Why a socket, not the admin HTTPS port.</b> The admin port has its own certificate and its own per-connection
/// source allowlist. Proxying a public name to it would make every client look like loopback, sharing one throttle bucket and
/// defeating an allowlist that names loopback (PLAN §3.1). The internal listener is reachable only by this process (the run
/// directory is 0700 and the socket 0600) and carries <b>no TLS</b>: plaintext stays inside the process boundary, or through
/// the loopback fallback's socket, where it is also visible to this machine's owner only.</para>
/// <para><b>Authenticated hop.</b> The listener can't rely on the peer address: in TCP fallback mode anything on the machine can
/// reach it, and in both modes a local process can. The public proxy therefore stamps <see cref="HeaderName"/> with
/// <see cref="Secret"/> on the admin route only (and removes a client's copy everywhere); the admin app refuses an internal
/// request that doesn't carry it before trusting any forwarded header.</para>
/// <para><b>Fallback.</b> A Unix socket is chosen only after a probe on a temporary sibling name succeeds; where it does not (a
/// platform without Unix-domain sockets, a path too long for <c>sockaddr_un</c>), the listener uses
/// <c>127.0.0.1:&lt;ephemeral&gt;</c>. The chosen port is recorded when the admin app has bound and the proxy's connect callback
/// waits for it briefly, so the fallback also works when the public app starts first. <see cref="FallbackReason"/> says why the
/// fallback happened and is logged at Warning by the server.</para>
/// <para><b>Stale sockets.</b> The real socket path is never deleted blindly: connecting to it first distinguishes a live second
/// instance (startup fails with <see cref="AdminInternalStartupException"/>) from a stale file, and only the latter is removed.
/// The probe uses a sibling name; <see cref="Dispose"/> removes the socket only while its identity is still the one this process
/// bound. On Linux the bound socket's inode is held open for the process's life: a filesystem may hand the inode number of a
/// deleted file straight to the next one (tmpfs and overlayfs do), so comparing numbers alone is not an identity, but the open
/// handle keeps <i>our</i> inode allocated, and then an equal number can only be the same file.</para>
/// <para>This type is in Core because the proxy (the connect callback and the secret stamp), the admin app (the middleware that
/// trusts forwarded headers on it only) and the composition root all need the same instance; it has no ASP.NET dependency.</para>
/// </remarks>
public sealed class AdminInternalListener : IDisposable
{
    /// <summary>The socket's file name inside <c>&lt;data dir&gt;/run</c>.</summary>
    public const string SocketFileName = "admin.sock";

    /// <summary>The header the public proxy stamps on every admin route, and no other upstream ever sees.</summary>
    public const string HeaderName = "X-Paduraksa-Internal";

    /// <summary>
    /// The symbolic upstream an admin host's YARP cluster points at (the proxy's <c>__admin</c> cluster). No name is ever
    /// resolved: the proxy's connect callback ignores the destination and connects to this listener.
    /// </summary>
    public const string UpstreamHost = "admin.internal";

    /// <summary>How long a connection waits for the loopback fallback's port before failing.</summary>
    public static readonly TimeSpan BindWait = TimeSpan.FromSeconds(5);

    private const int SecretBytes = 32;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxIno = 0x100;
    private const uint StatxUid = 0x8;
    private const uint StatxIdentity = StatxIno | StatxUid;

    // Linux open(2) flags (asm-generic values, the same on x86-64 and arm64). O_PATH opens even a socket, which is
    // what lets Dispose pin the socket's inode without reading it.
    private const int OpenPath = 0x200000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;

    private readonly string? _socketPath;
    private readonly TaskCompletionSource<IPEndPoint> _loopback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _secretText;
    private readonly byte[] _secretUtf8;
    private SocketIdentity? _boundIdentity;
    private SafeFileHandle? _identityPin;
    private int _disposed;

    /// <summary>
    /// Picks the listener endpoint for <paramref name="dataDirectory"/>: a Unix socket when one can be bound, else loopback TCP.
    /// </summary>
    /// <param name="dataDirectory">The process's data directory; the socket goes in its <c>run</c> subdirectory.</param>
    /// <param name="enabled"><c>Paduraksa:Listen:AdminInternal</c>. False: no internal listener at all.</param>
    /// <exception cref="AdminInternalStartupException">Another instance serves the socket, or the run directory is unsafe.</exception>
    public AdminInternalListener(string dataDirectory, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        byte[] secret = RandomNumberGenerator.GetBytes(SecretBytes);
        _secretText = Convert.ToHexString(secret);
        _secretUtf8 = Encoding.UTF8.GetBytes(_secretText);
        Enabled = enabled;
        if (!enabled)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            FallbackReason = new PlatformNotSupportedException("Unix-domain sockets are not used on Windows.");
            return;
        }

        string runDirectory = Path.Combine(dataDirectory, "run");
        string socketPath = Path.Combine(runDirectory, SocketFileName);
        try
        {
            PrepareRunDirectory(runDirectory);
            OwnershipCheckSkipped = !OperatingSystem.IsLinux() || UnixOwnership.Unavailable;

            // A live socket belongs to another instance: fail loudly instead of unlinking it from under that process.
            if (IsLiveSocket(socketPath))
            {
                throw new AdminInternalStartupException(
                    $"The admin internal socket '{socketPath}' is already served by another Paduraksa process. Stop it first, or give this process its own data directory.");
            }

            DeleteStaleSocket(socketPath);
            if (TryProbeUnixSocket(runDirectory, out Exception? probeError))
            {
                _socketPath = socketPath;
                UsesUnixSocket = true;
                return;
            }

            FallbackReason = probeError;
        }
        catch (AdminInternalStartupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No usable socket: the loopback fallback is always available, and the admin app logs what happened.
            _socketPath = null;
            UsesUnixSocket = false;
            FallbackReason = ex;
        }
    }

    /// <summary>False: the process has no internal admin listener, and an admin host can't compile.</summary>
    public bool Enabled { get; }

    /// <summary>The Unix socket's path; null with the loopback fallback (or when disabled).</summary>
    public string? SocketPath => _socketPath;

    /// <summary>True: the listener is the Unix socket at <see cref="SocketPath"/>; false: loopback TCP, port unknown until bind.</summary>
    public bool UsesUnixSocket { get; }

    /// <summary>Why the loopback fallback was chosen; null when the Unix socket is used (or the listener is disabled).</summary>
    public Exception? FallbackReason { get; }

    /// <summary>True when the run directory's owner couldn't be checked (the platform exposes no statx owner).</summary>
    public bool OwnershipCheckSkipped { get; private set; }

    /// <summary>The per-process hop secret (hex). Never logged and never returned in a response.</summary>
    public string Secret => _secretText;

    /// <summary>The fallback's loopback port once the admin app has bound; null before that.</summary>
    public IPEndPoint? LoopbackEndPoint => _loopback.Task.IsCompletedSuccessfully ? _loopback.Task.Result : null;

    /// <summary>Whether <paramref name="value"/> is this process's hop secret, compared in fixed time.</summary>
    public bool IsValidSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != _secretText.Length)
        {
            return false;
        }

        byte[] provided = Encoding.UTF8.GetBytes(value);
        return CryptographicOperations.FixedTimeEquals(provided, _secretUtf8);
    }

    /// <summary>Records the OS-assigned port of the loopback fallback (called once the admin app has started).</summary>
    public void BindLoopback(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _loopback.TrySetResult(new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>
    /// Records the identity of the socket Kestrel just bound, so <see cref="Dispose"/> can tell it from a socket another process
    /// later created at the same path.
    /// </summary>
    public void RecordBoundSocket()
    {
        if (!UsesUnixSocket || _socketPath is null)
        {
            return;
        }

        _boundIdentity = SocketIdentity.Read(_socketPath);
        Interlocked.Exchange(ref _identityPin, null)?.Dispose();
        _identityPin = TryPinInode(_socketPath);
    }

    /// <summary>
    /// The proxy's <see cref="SocketsHttpHandler.ConnectCallback"/>: opens a connection to the socket, or to the fallback's port
    /// once it is known. The destination YARP asks for is deliberately ignored.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Enabled)
        {
            throw new InvalidOperationException("The admin internal listener is disabled; an admin host can't be proxied.");
        }

        if (UsesUnixSocket)
        {
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath!), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        IPEndPoint endPoint = await WaitForLoopbackAsync(cancellationToken).ConfigureAwait(false);
        Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await tcp.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(tcp, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Removes the Unix socket file only if it is still the socket this process bound (the run directory stays).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SafeFileHandle? pin = Interlocked.Exchange(ref _identityPin, null);
        try
        {
            if (!UsesUnixSocket || _socketPath is null || _boundIdentity is not { } bound)
            {
                return;
            }

            if (bound.Matches(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the next start's stale-socket check handles it.
                }
            }
        }
        finally
        {
            pin?.Dispose();
        }
    }

    private async ValueTask<IPEndPoint> WaitForLoopbackAsync(CancellationToken cancellationToken)
    {
        if (_loopback.Task.IsCompletedSuccessfully)
        {
            return _loopback.Task.Result;
        }

        try
        {
            return await _loopback.Task.WaitAsync(BindWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException("The admin internal listener's loopback port was never bound; the admin app isn't ready.");
        }
    }

    /// <summary>
    /// Creates the run directory 0700 and refuses one that is a symlink or (where ownership can be read) not the process's.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void PrepareRunDirectory(string runDirectory)
    {
        if (Directory.Exists(runDirectory) && new DirectoryInfo(runDirectory).LinkTarget is not null)
        {
            throw new AdminInternalStartupException($"The admin internal run directory '{runDirectory}' is a symlink; refusing to use it.");
        }

        if (Directory.Exists(runDirectory) && !UnixOwnership.IsOwnedByCurrentUser(runDirectory))
        {
            throw new AdminInternalStartupException($"The admin internal run directory '{runDirectory}' is not owned by this process's user; refusing to use it.");
        }

        Directory.CreateDirectory(runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A bound, listening Unix socket at <paramref name="socketPath"/>: another instance is live.</summary>
    private static bool IsLiveSocket(string socketPath)
    {
        try
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(250));
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            // It exists but doesn't answer: treat it as live rather than steal a path another process may be using.
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes a socket file that a failed connect proved stale; a missing one is fine.</summary>
    private static void DeleteStaleSocket(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
        }
        catch (FileNotFoundException)
        {
        }
    }

    /// <summary>Probes Unix-socket support on a temporary sibling name, never on the real path.</summary>
    private static bool TryProbeUnixSocket(string runDirectory, out Exception? error)
    {
        string probePath = Path.Combine(runDirectory, $".p{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}");
        try
        {
            using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Bind(new UnixDomainSocketEndPoint(probePath));
            probe.Listen(1);
            probe.Close();
            File.Delete(probePath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            TryDelete(probePath);
            error = ex;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>The identity of the socket file, so dispose never deletes a successor's socket.</summary>
    /// <remarks>
    /// Inode and device are only an identity while this process holds the socket's inode open (<see cref="TryPinInode"/>):
    /// a filesystem can reuse a freed inode number immediately, and the successor would then look like us.
    /// </remarks>
    private readonly record struct SocketIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor, DateTime LastWriteUtc)
    {
        public static SocketIdentity Read(string path)
        {
            if (OperatingSystem.IsLinux() && TryStatx(path, out Statx stat))
            {
                return new SocketIdentity(stat.Ino, stat.DevMajor, stat.DevMinor, default);
            }

            return new SocketIdentity(0, 0, 0, File.GetLastWriteTimeUtc(path));
        }

        public bool Matches(string path)
        {
            if (Inode != 0 && TryStatx(path, out Statx stat))
            {
                return stat.Ino == Inode && stat.DevMajor == DeviceMajor && stat.DevMinor == DeviceMinor;
            }

            return Inode == 0 && File.GetLastWriteTimeUtc(path) == LastWriteUtc;
        }
    }

    /// <summary>Enough of <c>struct statx</c> to identify a file; the layout is stable on Linux.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(20)]
        public uint Uid;

        [FieldOffset(32)]
        public ulong Ino;

        [FieldOffset(136)]
        public uint DevMajor;

        [FieldOffset(140)]
        public uint DevMinor;
    }

    /// <summary>
    /// Opens the bound socket's inode with <c>O_PATH</c> and keeps the handle, so the inode number recorded for it cannot be
    /// reused by a successor while this process lives. Null where that isn't possible (not Linux, or no <c>O_PATH</c>): the
    /// identity check then remains best effort.
    /// </summary>
    private static SafeFileHandle? TryPinInode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            int fd = Open(path, OpenPath | OpenNoFollow | OpenCloseOnExec);
            return fd >= 0 ? new SafeFileHandle((IntPtr)fd, ownsHandle: true) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static bool TryStatx(string path, out Statx statx)
    {
        try
        {
            return StatxNative(AtFdcwd, path, AtSymlinkNoFollow, StatxIdentity, out statx) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            statx = default;
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxNative(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buffer);

    private static class UnixOwnership
    {
        public static bool IsOwnedByCurrentUser(string path)
        {
            if (!OperatingSystem.IsLinux() || !TryStatx(path, out Statx stat))
            {
                // The owner isn't exposed by the BCL and statx is Linux-only; on other Unixes the symlink check above still applies.
                Unavailable = true;
                return true;
            }

            try
            {
                return stat.Uid == GetEuid();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Unavailable = true;
                return true;
            }
        }

        public static bool Unavailable { get; private set; }

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint GetEuid();
    }
}

/// <summary>The internal listener can't start: another process serves its socket, or its run directory is unsafe.</summary>
public sealed class AdminInternalStartupException : Exception
{
    public AdminInternalStartupException(string message)
        : base(message)
    {
    }

    public AdminInternalStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
