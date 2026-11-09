namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Where the access log goes (PLAN §7.4): one line at a time, written by <c>AccessLogDrainService</c> on its own
/// thread, never on a request's.
/// </summary>
/// <remarks>
/// <para><b>The line has no trailing newline</b>: the sink writes it as one line and owns the separator, so a writer that
/// needs <c>\n</c>, <c>\r\n</c> or a length prefix decides for itself.</para>
/// <para><see cref="Flush"/> is called when the process stops (and may be called at other times): whatever the sink
/// buffers must reach its destination. The default sink is <see cref="NullAccessLogSink"/>. M6's rotating file writer
/// (<c>m6-log-files</c>) has its own shape; it reaches this interface through a small adapter that is wired in once both
/// branches merge, so neither side has to be rewritten for the other.</para>
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

/// <summary>The sink a process has until a real one is configured: counts nothing, writes nothing, and costs nothing.</summary>
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
