using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>The file system as the checks see it, so a test can describe a host without making one.</summary>
internal interface IDoctorFileSystem
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>Whether this user can create files in the directory (checked with <c>access(2)</c>: nothing is created).</summary>
    bool CanWriteDirectory(string path);

    /// <summary>The Unix mode of a file, or null when it is missing or the platform has none.</summary>
    UnixFileMode? Mode(string path);

    /// <summary>Free and total bytes of the volume holding <paramref name="path"/>, or null when unknown.</summary>
    DiskSpace? Space(string path);

    string CurrentUser { get; }
}

internal readonly record struct DiskSpace(long FreeBytes, long TotalBytes);

/// <summary>What binding a port told.</summary>
internal enum PortState
{
    Free,

    /// <summary>Something holds it (address in use).</summary>
    InUse,

    /// <summary>Binding is refused to this user (a port below 1024 without the capability): says nothing about whether it is free.</summary>
    Denied,

    /// <summary>Could not be tested (no IPv6, an address this machine does not have).</summary>
    Unknown,
}

/// <summary>Tests a listener's port by binding it and letting go at once.</summary>
internal interface IPortProbe
{
    PortState Probe(IPAddress address, int port, bool udp);
}

/// <summary>An answer to one GET, or why there was none.</summary>
/// <param name="Status">The HTTP status, or null when the request did not complete.</param>
/// <param name="Body">The body (text, at most a few KiB).</param>
/// <param name="Date">The <c>Date</c> response header.</param>
/// <param name="Error">Why there was no answer.</param>
internal sealed record HttpAnswer(int? Status, string? Body, DateTimeOffset? Date, string? Error)
{
    public bool Answered => Status is not null;
}

/// <summary>The one HTTP client the doctor has: loopback probes of the running server, and the clock's reference request.</summary>
internal interface IDoctorHttp
{
    /// <summary>
    /// GETs <paramref name="uri"/>. <paramref name="trustAnyCertificate"/> accepts any certificate: only for loopback, where the admin's certificate is
    /// signed by the instance's own CA, which this process does not load. <paramref name="bearerToken"/> is an API token, only ever sent to loopback.
    /// </summary>
    Task<HttpAnswer> GetAsync(Uri uri, bool trustAnyCertificate, string? bearerToken, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Whether libmsquic loads, which HTTP/3 needs.</summary>
internal interface IQuicProbe
{
    bool IsSupported { get; }
}

/// <summary>The real host.</summary>
internal sealed class SystemFileSystem : IDoctorFileSystem
{
    private const int WriteOk = 2;
    private const int ExecuteOk = 1;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool CanWriteDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return Access(path, WriteOk | ExecuteOk) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    public UnixFileMode? Mode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.GetUnixFileMode(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DiskSpace? Space(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);

            // The volume is the mount whose root is the longest prefix of the path: new DriveInfo(path) needs the mount point itself.
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is null ? null : new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string CurrentUser => Environment.UserName;

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}

internal sealed class SystemPortProbe : IPortProbe
{
    public PortState Probe(IPAddress address, int port, bool udp)
    {
        var state = Bind(address, port, udp);

        // A host with IPv6 switched off (some containers) refuses the wildcard [::]: Kestrel then binds IPv4 alone, and so does this.
        return state == PortState.Unknown && address.Equals(IPAddress.IPv6Any) ? Bind(IPAddress.Any, port, udp) : state;
    }

    private static PortState Bind(IPAddress address, int port, bool udp)
    {
        // Dual-mode on [::], like Kestrel's wildcard bind: a plain IPv4 socket would miss a listener that holds the port on IPv6 only.
        var family = address.AddressFamily;
        try
        {
            using Socket socket = new(family, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
            if (family == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = address.Equals(IPAddress.IPv6Any);
            }

            socket.Bind(new IPEndPoint(address, port));
            return PortState.Free;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return PortState.InUse;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return PortState.Denied;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException)
        {
            return PortState.Unknown;
        }
    }
}

internal sealed class SystemQuicProbe : IQuicProbe
{
#pragma warning disable CA2252 // QuicListener is a stable API in .NET 10; the analyzer still flags it on some SDKs.
    public bool IsSupported => QuicListener.IsSupported;
#pragma warning restore CA2252
}

internal sealed class SystemDoctorHttp : IDoctorHttp
{
    private const int MaxBodyBytes = 4096;

    public async Task<HttpAnswer> GetAsync(Uri uri, bool trustAnyCertificate, string? bearerToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        if (trustAnyCertificate)
        {
            // Loopback only (the callers pass it for loopback addresses): see DoctorCommands, the admin's certificate is the instance's own CA's.
#pragma warning disable CA5359 // Do not disable certificate validation: deliberate, loopback-only, and only a status is read.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        using HttpClient client = new(handler) { Timeout = timeout };
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            if (bearerToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return new HttpAnswer((int)response.StatusCode, body, response.Headers.Date, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpAnswer(null, null, null, ex.Message);
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxBodyBytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
    }
}
