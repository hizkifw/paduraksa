using Paduraksa.Proxy.Logging;

namespace Paduraksa.Server.Logging;

/// <summary>
/// The access log's sink (PLAN §7.4): hands each formatted line to <c>access.log</c>'s writer
/// (<see cref="ServerLogFiles.Access"/>), which appends it, rotates the file when it has to and never blocks the drain (a
/// full queue drops the line and counts it).
/// </summary>
/// <remarks>
/// The writer owns the line separator: it writes a newline after every line it is given, and the formatter produces none, so
/// the line is passed through unchanged. The writer belongs to <see cref="ServerLogFiles"/>, which disposes it after the
/// hosts have stopped (that is, after the drain's last flush), so this adapter owns nothing and is not disposable.
/// </remarks>
internal sealed class AccessLogFileSink : IAccessLogSink
{
    private readonly RotatingLogFile _file;

    /// <param name="files">The process's log files; the access log's writer is the one this sink feeds.</param>
    public AccessLogFileSink(ServerLogFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _file = files.Access;
    }

    public void WriteLine(ReadOnlySpan<char> line) => _file.WriteLine(line);

    public void Flush() => _file.Flush();
}
