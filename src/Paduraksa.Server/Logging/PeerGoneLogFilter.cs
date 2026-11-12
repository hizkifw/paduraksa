using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;

namespace Paduraksa.Server.Logging;

/// <summary>
/// Kestrel logs a client that vanished mid-write (a phone off the network, a laptop asleep, a NAT entry gone) at Error, with
/// two full stacks per connection: once when the HTTP/2 GOAWAY can't be flushed and once when the TLS stream's dispose flushes
/// again. That is ordinary traffic, not a fault, and it buried the real errors in <c>error.log</c>. Such an entry is written at
/// Debug instead, as one line naming the cause, and counted (<see cref="Count"/>, exported as
/// <c>paduraksa.connection.errors{kind="peer_gone"}</c>) so a sudden rise still shows.
/// <para>
/// Narrow on purpose: only Kestrel's categories, and only an exception chain that is nothing but the transport failing under
/// a write. Anything else Kestrel reports, and every exception from Paduraksa's own code, keeps its level and its stack.
/// </para>
/// </summary>
internal sealed class PeerGoneLogFilter
{
    private const string KestrelCategoryPrefix = "Microsoft.AspNetCore.Server.Kestrel";

    // OpenSSL's exception type is internal to System.Net.Security, so it is recognised by name. SSL_ERROR_SYSCALL (5) means
    // the socket under TLS failed, not TLS itself; the runtime words it either way depending on where the write failed.
    private const string OpenSslExceptionType = "Interop+OpenSsl+SslException";

    private long _count;

    public long Count => Interlocked.Read(ref _count);

    /// <summary>
    /// Whether this entry is a client going away; when it is, <paramref name="cause"/> is the innermost message (what the
    /// one-line entry names) and the entry is counted.
    /// </summary>
    public bool IsPeerGone(string category, LogLevel level, Exception? exception, out string cause)
    {
        cause = string.Empty;
        if (level < LogLevel.Warning
            || exception is null
            || !category.StartsWith(KestrelCategoryPrefix, StringComparison.Ordinal)
            || !IsPeerGone(exception, out var root))
        {
            return false;
        }

        cause = root.Message;
        Interlocked.Increment(ref _count);
        return true;
    }

    /// <summary>
    /// True when every exception in the chain is a transport wrapper (<see cref="IOException"/>) or a transport failure, and the
    /// chain ends in one that means the peer is gone.
    /// </summary>
    internal static bool IsPeerGone(Exception exception, out Exception root)
    {
        root = exception;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            root = current;
            if (IsPeerGoneCause(current))
            {
                return true;
            }

            if (current is not IOException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsPeerGoneCause(Exception exception) => exception switch
    {
        ConnectionResetException or ConnectionAbortedException => true,
        SocketException socket => socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.TimedOut or SocketError.Shutdown or SocketError.OperationAborted or SocketError.NotConnected
            or SocketError.NetworkReset or SocketError.NetworkDown or SocketError.NetworkUnreachable
            or SocketError.HostUnreachable or SocketError.HostDown,
        _ => string.Equals(exception.GetType().FullName, OpenSslExceptionType, StringComparison.Ordinal)
            && (exception.Message.Contains("SSL_ERROR_SYSCALL", StringComparison.Ordinal)
                || exception.Message.EndsWith("error - 5", StringComparison.Ordinal)),
    };
}
