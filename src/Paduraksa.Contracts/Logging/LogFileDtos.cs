namespace Paduraksa.Contracts.Logging;

/// <summary>
/// One file in the logs directory: its name, which log it belongs to, its size, and when it was last written.
/// Served by the API's log list and shown by the dashboard's log page.
/// </summary>
/// <param name="Name">The file's name inside the logs directory, e.g. <c>error.log</c> or <c>access-20261109T120000Z.log.gz</c>.</param>
/// <param name="Log">The log it belongs to: <c>access</c> or <c>error</c>.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="LastModified">When it was last written, in UTC.</param>
/// <param name="Compressed">True for a rotated file that has been gzipped.</param>
public sealed record LogFileSummary(string Name, string Log, long Size, DateTimeOffset LastModified, bool Compressed);

/// <summary>
/// The last lines of a log file, read without loading the whole file: at most 1000 lines out of the last 1 MiB (a rotated
/// file is decompressed up to a cap first), optionally only those matching a filter.
/// </summary>
/// <param name="Name">The file these lines come from.</param>
/// <param name="Log">The log it belongs to: <c>access</c> or <c>error</c>.</param>
/// <param name="Lines">The lines, oldest first, each at most 4096 characters.</param>
/// <param name="Truncated">True when the file (or the part that was read) held more than <paramref name="Lines"/> carries.</param>
/// <param name="FileSize">The file's size in bytes.</param>
public sealed record LogFileTail(string Name, string Log, IReadOnlyList<string> Lines, bool Truncated, long FileSize);
