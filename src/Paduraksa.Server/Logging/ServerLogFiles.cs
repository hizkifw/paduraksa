using Paduraksa.Core.Logging;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Logging;

/// <summary>
/// The process's log files: one writer per log (access and error) and the provider that feeds the error log. Built once, in
/// <see cref="Hosting.SharedServices"/>, and disposed after the hosts have stopped, which flushes what is buffered.
/// </summary>
internal sealed class ServerLogFiles : IDisposable
{
    private int _disposed;

    public ServerLogFiles(PaduraksaLogOptions options, string dataDirectory, TimeProvider time, ILogger report)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(report);

        Directory = string.IsNullOrWhiteSpace(options.ResolvedDirectory)
            ? Path.Combine(dataDirectory, LogFileNames.DefaultDirectoryName)
            : options.ResolvedDirectory;
        Error = new RotatingLogFile(Settings(LogFileKind.Error, options), time, report);
        Access = new RotatingLogFile(Settings(LogFileKind.Access, options), time, report);
        ErrorProvider = new ErrorLogProvider(Error);
    }

    public string Directory { get; }

    /// <summary><c>error.log</c>: the process's own entries at <c>Paduraksa:Logs:ErrorLevel</c> and above.</summary>
    public RotatingLogFile Error { get; }

    /// <summary><c>access.log</c>: one line per request, written by the access log's background drain.</summary>
    public RotatingLogFile Access { get; }

    /// <summary>The error log's provider; added to the process's one logger factory.</summary>
    public ErrorLogProvider ErrorProvider { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Disposing a writer joins its threads and flushes the buffer, so this is the log's last write.
        Error.Dispose();
        Access.Dispose();
    }

    private LogFileWriterSettings Settings(LogFileKind kind, PaduraksaLogOptions options) => new(
        kind,
        Directory,
        options.MaxFileSize,
        options.RetainedFiles,
        options.MaxAge,
        options.MaxTotalSize);
}
