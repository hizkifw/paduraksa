namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Where the access log goes (PLAN §7.4): one line at a time, written by <c>AccessLogDrainService</c> on its own
/// thread, never on a request's.
/// </summary>
/// <remarks>
/// <para><b>The line has no trailing newline</b>: the sink writes it as one line and owns the separator, so a writer that
/// needs <c>\n</c>, <c>\r\n</c> or a length prefix decides for itself.</para>
/// <para><see cref="Flush"/> is called when the process stops (and may be called at other times): whatever the sink
/// buffers must reach its destination.</para>
/// <para><b>What a process uses.</b> <c>Paduraksa.Server.Logging.AccessLogFileSink</c>: an adapter over
/// <c>access.log</c>'s rotating writer (<c>ServerLogFiles.Access</c>), which adds the newline, rotates and compresses the
/// file and never blocks the drain. A test app registers <see cref="NullAccessLogSink"/> or its own capturing sink through
/// <c>SharedServices.Builder.Replace</c>, so nothing has to be configured for lines to be read directly.</para>
/// <para>A sink that throws is logged and skipped, never retried in place: a full disk must not stall the drain or the
/// process.</para>
/// </remarks>
public interface IAccessLogSink
{
    /// <summary>Writes one formatted line (no trailing newline).</summary>
    void WriteLine(ReadOnlySpan<char> line);

    /// <summary>Pushes everything buffered to the destination.</summary>
    void Flush();
}

/// <summary>A sink that writes nothing, for a host that registered no writer (a test app, whose tests read the ring or swap
/// in their own sink). Counts nothing, writes nothing, costs nothing.</summary>
public sealed class NullAccessLogSink : IAccessLogSink
{
    /// <summary>The one instance (the interface has no state worth keeping per caller).</summary>
    public static NullAccessLogSink Instance { get; } = new();

    private NullAccessLogSink()
    {
    }

    public void WriteLine(ReadOnlySpan<char> line)
    {
    }

    public void Flush()
    {
    }
}
