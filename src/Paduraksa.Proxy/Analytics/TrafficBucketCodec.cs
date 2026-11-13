using System.IO.Compression;

namespace Paduraksa.Proxy.Analytics;

/// <summary>A bucket as it is stored: the visitors sketch on its own (a chart reads every step's, and not the rest), and everything else compressed.</summary>
/// <param name="Visitors">The <see cref="HyperLogLog"/>, sparse when it holds few registers.</param>
/// <param name="Data">A format byte, the length of the plain bytes, then the plain bytes Brotli-compressed.</param>
public sealed record EncodedTrafficBucket(byte[] Visitors, byte[] Data);

/// <summary>
/// The stored form of a <see cref="TrafficBucket"/> (PLAN §7.4, analytics): little-endian varints and length-prefixed UTF-8 text,
/// Brotli-compressed, with a format byte up front so a later release can read an older row. Decoding is bounded and total: a blob
/// that is cut, corrupt or lies about its sizes yields <see langword="null"/>, never an exception or an allocation the blob chose.
/// </summary>
public static class TrafficBucketCodec
{
    // 2 added the request bytes after the response bytes; 1 is still read, as a bucket with none.
    private const byte Format = 2;

    private const byte FormatWithoutBytesIn = 1;

    /// <summary>The most a decoded blob may take; a full bucket is tens of KiB, so this only ever refuses a corrupt one.</summary>
    private const int MaxPlainBytes = 8 * 1024 * 1024;

    public static EncodedTrafficBucket Encode(TrafficBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ByteWriter visitors = new();
        bucket.Visitors.Write(visitors);

        ByteWriter plain = new();
        plain.Varint(bucket.Requests);
        plain.Varint(bucket.BytesOut);
        plain.Varint(bucket.BytesIn);
        plain.Varint(bucket.ServerErrors);
        WriteAll(plain, bucket.StatusClasses);
        WriteAll(plain, bucket.Protocols);
        WriteAll(plain, bucket.Devices);
        WriteAll(plain, bucket.BotClasses);
        bucket.Statuses.Write(plain);
        bucket.Methods.Write(plain);
        bucket.Browsers.Write(plain);
        bucket.OperatingSystems.Write(plain);
        bucket.BotNames.Write(plain);
        bucket.IgnoredRobots.Write(plain);
        bucket.Paths.Write(plain);
        bucket.Referers.Write(plain);
        bucket.UserAgents.Write(plain);
        bucket.Largest.Write(plain);

        var source = plain.Written;
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, compressed, out var written, quality: 4, window: 20))
        {
            throw new InvalidOperationException("Compressing an analytics bucket failed.");
        }

        ByteWriter data = new();
        data.Add(Format);
        data.Varint(source.Length);
        data.Add(compressed.AsSpan(0, written));
        return new EncodedTrafficBucket(visitors.Written.ToArray(), data.Written.ToArray());
    }

    /// <summary>The bucket, or null when either blob is not one this release wrote.</summary>
    public static TrafficBucket? Decode(EncodedTrafficBucket encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (!HyperLogLog.TryRead(encoded.Visitors, out var visitors, out _))
        {
            return null;
        }

        ByteReader header = new(encoded.Data);
        var format = header.Byte();
        if (format is not (Format or FormatWithoutBytesIn))
        {
            return null;
        }

        var plainLength = header.Varint();
        if (header.Failed || plainLength is < 0 or > MaxPlainBytes)
        {
            return null;
        }

        var plain = new byte[plainLength];
        if (!BrotliDecoder.TryDecompress(header.Rest, plain, out var written) || written != plainLength)
        {
            return null;
        }

        ByteReader reader = new(plain);
        var requests = reader.Varint();
        var bytesOut = reader.Varint();
        var bytesIn = format == FormatWithoutBytesIn ? 0 : reader.Varint();
        var serverErrors = reader.Varint();
        var statusClasses = ReadAll(ref reader, 6);
        var protocols = ReadAll(ref reader, 5);
        var devices = ReadAll(ref reader, 5);
        var botClasses = ReadAll(ref reader, 8);
        var statuses = BoundedCounter.Read(ref reader);
        var methods = BoundedCounter.Read(ref reader);
        var browsers = BoundedCounter.Read(ref reader);
        var operatingSystems = BoundedCounter.Read(ref reader);
        var botNames = BoundedCounter.Read(ref reader);
        var ignoredRobots = BoundedCounter.Read(ref reader);
        var paths = HeavyHitters.Read(ref reader);
        var referers = HeavyHitters.Read(ref reader);
        var agents = HeavyHitters.Read(ref reader);
        var largest = HeavyHitters.Read(ref reader);
        if (reader.Failed || reader.Remaining != 0
            || statuses is null || methods is null || browsers is null || operatingSystems is null || botNames is null || ignoredRobots is null
            || paths is null || referers is null || agents is null || largest is null)
        {
            return null;
        }

        return new TrafficBucket(
            requests, bytesOut, bytesIn, serverErrors, statusClasses, statuses, methods, protocols, devices, botClasses, browsers, operatingSystems, botNames, ignoredRobots,
            visitors, paths, referers, agents, largest);
    }

    /// <summary>Just the visitors sketch, for the charts that read a sketch per step and nothing else; null when it is not one.</summary>
    public static HyperLogLog? DecodeVisitors(byte[] visitors)
    {
        ArgumentNullException.ThrowIfNull(visitors);
        return HyperLogLog.TryRead(visitors, out var sketch, out var read) && read == visitors.Length ? sketch : null;
    }

    private static void WriteAll(ByteWriter destination, long[] values)
    {
        foreach (var value in values)
        {
            destination.Varint(value);
        }
    }

    private static long[] ReadAll(ref ByteReader reader, int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.Varint();
        }

        return values;
    }
}
