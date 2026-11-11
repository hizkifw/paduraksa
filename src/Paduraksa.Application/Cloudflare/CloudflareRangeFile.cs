using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Application.Cloudflare;

/// <summary>
/// <c>&lt;data dir&gt;/cloudflare-ranges.json</c>: the last accepted list of Cloudflare's edge ranges with its fetch time, so a
/// restart doesn't fall back to the built-in list. Not config (no revision, no audit event) and no secret: public ranges.
/// </summary>
/// <remarks>
/// A write is atomic (a temp file beside the target, then a rename), and a read never trusts the file: it goes through the
/// same checks as a fetched list (<see cref="EdgeRangeList.TryCreate"/>), so a damaged or edited file is ignored with a log line
/// and the built-in list holds until the next fetch. A symbolic link at the target is never followed.
/// </remarks>
public sealed partial class CloudflareRangeFile(string path, ILogger logger)
{
    /// <summary>The file name inside the data directory.</summary>
    public const string FileName = "cloudflare-ranges.json";

    private const string Format = "paduraksa.dev/cloudflare-ranges/v1";
    private const int MaxFileLength = 64 * 1024;

    private readonly ILogger _logger = logger;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>The stored list, or null when there is none or it can't be used.</summary>
    public EdgeRangeList? Load()
    {
        try
        {
            FileInfo file = new(Path);
            if (file.LinkTarget is not null || !file.Exists)
            {
                return null;
            }

            using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileLength)
            {
                LogUnusable(_logger, Path, "it is larger than expected");
                return null;
            }

            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != Format)
            {
                LogUnusable(_logger, Path, "its format is unknown");
                return null;
            }

            List<string> blocks = [];
            foreach (var name in (string[])["ipv4", "ipv6"])
            {
                foreach (var block in root.GetProperty(name).EnumerateArray())
                {
                    blocks.Add(block.GetString() ?? string.Empty);
                }
            }

            var fetchedAt = root.GetProperty("fetchedAt").GetDateTimeOffset();
            if (EdgeRangeList.TryCreate(blocks, fetchedAt, out var list, out var reason))
            {
                return list;
            }

            LogUnusable(_logger, Path, reason);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            LogUnusable(_logger, Path, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Writes <paramref name="list"/> atomically. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// when it can't; the caller keeps the list in memory and tries again next time.</summary>
    public void Save(EdgeRangeList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(directory, $"{System.IO.Path.GetFileName(Path)}.{System.IO.Path.GetRandomFileName()}.tmp");
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteString("fetchedAt", list.FetchedAt);
                writer.WriteStartArray("ipv4");
                foreach (var block in list.Blocks)
                {
                    if (!block.Contains(':', StringComparison.Ordinal))
                    {
                        writer.WriteStringValue(block);
                    }
                }

                writer.WriteEndArray();
                writer.WriteStartArray("ipv6");
                foreach (var block in list.Blocks)
                {
                    if (block.Contains(':', StringComparison.Ordinal))
                    {
                        writer.WriteStringValue(block);
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Replaces a symbolic link at the target rather than following it.
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring the stored Cloudflare edge ranges in {Path}: {Reason}. The built-in list holds until the next fetch.")]
    private static partial void LogUnusable(ILogger logger, string path, string reason);
}
