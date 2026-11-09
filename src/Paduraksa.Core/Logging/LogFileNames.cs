using System.Globalization;

namespace Paduraksa.Core.Logging;

/// <summary>Which log a file holds (PLAN §7.4).</summary>
public enum LogFileKind
{
    /// <summary>One line per request, written by the access log's background drain.</summary>
    Access = 1,

    /// <summary>The process's own Warning-and-above entries.</summary>
    Error = 2,
}

/// <summary>
/// The names of the log files under <c>&lt;data dir&gt;/logs</c> (PLAN §7.4), shared by the writer (Server) and the reader (Application and
/// the dashboard), so both agree on what a log file is and on which names may be served.
/// <para>
/// A log has one <b>live</b> file (<c>access.log</c>, <c>error.log</c>) that is appended to, and any number of <b>rotated</b> ones
/// (<c>access-20261109T120000Z.log</c>, gzip-compressed to <c>....log.gz</c> once the writer has moved on). Rotated names carry a UTC
/// timestamp that sorts chronologically, and a rotation that lands in the same second adds a <c>-2</c>, <c>-3</c>, ... suffix.
/// </para>
/// </summary>
public static class LogFileNames
{
    /// <summary>The directory under the data directory, when <c>Paduraksa:Logs:Directory</c> says nothing else.</summary>
    public const string DefaultDirectoryName = "logs";

    /// <summary>The longest name the writer produces (and the longest the dashboard serves).</summary>
    public const int MaxNameLength = 96;

    /// <summary>The access log's live file.</summary>
    public const string AccessLive = "access.log";

    /// <summary>The error log's live file.</summary>
    public const string ErrorLive = "error.log";

    /// <summary>The name's timestamp, as it appears in a rotated file: UTC, whole seconds, sortable.</summary>
    public const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Both logs' live files.</summary>
    public static IReadOnlyList<string> LiveFiles { get; } = [ErrorLive, AccessLive];

    /// <summary>The live file's name for <paramref name="kind"/>.</summary>
    public static string Live(LogFileKind kind) => kind switch
    {
        LogFileKind.Access => AccessLive,
        LogFileKind.Error => ErrorLive,
        _ => throw Undefined(kind),
    };

    /// <summary>The file-name stem of <paramref name="kind"/> (<c>access</c>, <c>error</c>), as a rotated name starts with it.</summary>
    public static string Stem(LogFileKind kind) => kind switch
    {
        LogFileKind.Access => "access",
        LogFileKind.Error => "error",
        _ => throw Undefined(kind),
    };

    /// <summary>Which log a live file is, or null when <paramref name="name"/> is no live file.</summary>
    public static LogFileKind? KindOfLive(string? name) => name switch
    {
        AccessLive => LogFileKind.Access,
        ErrorLive => LogFileKind.Error,
        _ => null,
    };

    /// <summary>
    /// A rotated file's name: <c>error-20261109T120000Z.log</c>. <paramref name="sequence"/> 1 is the first rotation in that second
    /// (no suffix); 2 and up add <c>-2</c>, so two rotations in one second can never share a name.
    /// </summary>
    public static string Rotated(LogFileKind kind, DateTimeOffset rotateAt, int sequence = 1)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A rotation's sequence starts at 1.");
        }

        string timestamp = rotateAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string suffix = sequence == 1 ? string.Empty : "-" + sequence.ToString(CultureInfo.InvariantCulture);
        return $"{Stem(kind)}-{timestamp}{suffix}.log";
    }

    /// <summary><paramref name="rotatedName"/> compressed: <c>error-20261109T120000Z.log.gz</c>.</summary>
    public static string Compressed(string rotatedName) => rotatedName + ".gz";

    /// <summary>
    /// Whether <paramref name="name"/> is a file this writer owns: a live name or a rotated one (compressed or not). False for
    /// anything with a separator, a parent reference or an unknown shape, so a request can never reach another file.
    /// </summary>
    public static bool IsLogFile(string? name) => name is not null && (KindOfLive(name) is not null || TryParseRotated(name, out _, out _, out _));

    /// <summary>
    /// Parses a rotated file's name (<c>error-20261109T120000Z-2.log</c>, <c>error-...log.gz</c>). False for a live file or any other name.
    /// </summary>
    public static bool TryParseRotated(string? name, out LogFileKind kind, out DateTimeOffset rotatedAt, out bool compressed) =>
        TryParseRotated(name, out kind, out rotatedAt, out _, out compressed);

    /// <summary>
    /// As above, and the rotation's <paramref name="sequence"/> within that second (1 for the unsuffixed name, 2 up for the suffixed
    /// ones), so two rotations of one second can be ordered.
    /// </summary>
    public static bool TryParseRotated(string? name, out LogFileKind kind, out DateTimeOffset rotatedAt, out int sequence, out bool compressed)
    {
        kind = default;
        rotatedAt = default;
        sequence = 1;
        compressed = false;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        ReadOnlySpan<char> rest = name;
        if (rest.EndsWith(".gz", StringComparison.Ordinal))
        {
            compressed = true;
            rest = rest[..^3];
        }

        if (!rest.EndsWith(".log", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[..^4];
        int dash = rest.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        if (!TryStem(rest[..dash], out kind))
        {
            return false;
        }

        ReadOnlySpan<char> stamp = rest[(dash + 1)..];
        int sequenceDash = stamp.IndexOf('-');
        if (sequenceDash >= 0)
        {
            ReadOnlySpan<char> suffix = stamp[(sequenceDash + 1)..];
            if (suffix.IsEmpty || suffix.Length > 9 || suffix[0] == '0'
                || !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 2)
            {
                return false;
            }

            stamp = stamp[..sequenceDash];
        }

        if (!DateTime.TryParseExact(
                stamp,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return false;
        }

        rotatedAt = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    private static bool TryStem(ReadOnlySpan<char> stem, out LogFileKind kind)
    {
        foreach (LogFileKind candidate in Enum.GetValues<LogFileKind>())
        {
            if (Stem(candidate).AsSpan().SequenceEqual(stem))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static ArgumentOutOfRangeException Undefined(LogFileKind kind) => new(nameof(kind), kind, "Undefined log file kind.");
}
