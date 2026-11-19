using System.Text;

using BenchmarkDotNet.Attributes;

using Paduraksa.TypedIds;

namespace Paduraksa.Benchmarks;

[TypedId("bench")]
public readonly partial record struct BenchId;

/// <summary>
/// The typed-ID codec and generators: base62 encode and decode of a <see cref="Guid"/> (22 chars) and a
/// <see cref="long"/> (11 chars), from chars and from UTF-8; prefixed <see cref="TypedIdCodec"/> format and parse; the
/// three generators; and the BCL's <see cref="Guid"/> equivalents (<c>NewGuid</c>, <c>CreateVersion7</c>,
/// <c>ToString</c>, <c>TryParse</c>) as references. <c>Guid.CreateVersion7</c> isn't monotonic within a millisecond;
/// ours is.
/// </summary>
[MemoryDiagnoser]
public class TypedIdBenchmarks
{
    private const string Prefix = "bench_";
    private static ReadOnlySpan<byte> Utf8Prefix => "bench_"u8;

    private readonly char[] _chars = new char[Prefix.Length + Base62.Width128];
    private readonly byte[] _utf8 = new byte[Prefix.Length + Base62.Width128];
    private readonly Int64OrderedIdGenerator _int64 = new(TimeProvider.System);

    private Guid _guid;
    private long _long;
    private string _guidText = "";
    private string _guidBase62 = "";
    private string _longBase62 = "";
    private string _prefixedGuid = "";
    private string _prefixedLong = "";
    private byte[] _guidBase62Utf8 = [];
    private byte[] _longBase62Utf8 = [];
    private byte[] _prefixedGuidUtf8 = [];
    private BenchId _id;

    [GlobalSetup]
    public void Setup()
    {
        _guid = UuidV7Generator.Shared.Next();
        _long = Int64OrderedIdGenerator.Shared.Next();
        _id = BenchId.New();
        _guidText = _guid.ToString();
        _guidBase62 = Base62.Encode(_guid);
        _longBase62 = Base62.Encode((ulong)_long);
        _prefixedGuid = _id.ToString();
        _prefixedLong = Prefix + _longBase62;
        _guidBase62Utf8 = Encoding.ASCII.GetBytes(_guidBase62);
        _longBase62Utf8 = Encoding.ASCII.GetBytes(_longBase62);
        _prefixedGuidUtf8 = Encoding.ASCII.GetBytes(_prefixedGuid);

        if (!Base62.TryDecodeGuid(_guidBase62, out var guid) || guid != _guid
            || !Base62.TryDecodeGuid(_guidBase62Utf8, out guid) || guid != _guid
            || !Base62.TryDecodeUInt64(_longBase62, out var number) || number != (ulong)_long
            || !Base62.TryDecodeUInt64(_longBase62Utf8, out number) || number != (ulong)_long
            || !TypedIdCodec.TryParse(_prefixedGuid, Prefix, out Guid prefixed) || prefixed != _id.Value
            || !TypedIdCodec.TryParse(_prefixedGuidUtf8, Utf8Prefix, out prefixed) || prefixed != _id.Value
            || !TypedIdCodec.TryParse(_prefixedLong, Prefix, out long prefixedLong) || prefixedLong != _long
            || !Guid.TryParse(_guidText, out _))
        {
            throw new InvalidOperationException("The inputs don't round-trip.");
        }
    }

    [Benchmark]
    public bool EncodeGuidChars() => Base62.TryEncode(_guid, _chars);

    [Benchmark]
    public bool EncodeGuidUtf8() => Base62.TryEncode(_guid, _utf8);

    [Benchmark]
    public bool EncodeInt64Chars() => Base62.TryEncode((ulong)_long, _chars);

    [Benchmark]
    public bool EncodeInt64Utf8() => Base62.TryEncode((ulong)_long, _utf8);

    [Benchmark]
    public bool DecodeGuidChars() => Base62.TryDecodeGuid(_guidBase62, out _);

    [Benchmark]
    public bool DecodeGuidUtf8() => Base62.TryDecodeGuid(_guidBase62Utf8, out _);

    [Benchmark]
    public bool DecodeInt64Chars() => Base62.TryDecodeUInt64(_longBase62, out _);

    [Benchmark]
    public bool DecodeInt64Utf8() => Base62.TryDecodeUInt64(_longBase62Utf8, out _);

    [Benchmark]
    public bool FormatPrefixedGuidChars() => TypedIdCodec.TryFormat(_guid, Prefix, _chars, out _);

    [Benchmark]
    public bool FormatPrefixedGuidUtf8() => TypedIdCodec.TryFormat(_guid, Utf8Prefix, _utf8, out _);

    [Benchmark]
    public bool FormatPrefixedInt64Chars() => TypedIdCodec.TryFormat(_long, Prefix, _chars, out _);

    [Benchmark]
    public bool FormatPrefixedInt64Utf8() => TypedIdCodec.TryFormat(_long, Utf8Prefix, _utf8, out _);

    [Benchmark]
    public bool ParsePrefixedGuidChars() => TypedIdCodec.TryParse(_prefixedGuid, Prefix, out Guid _);

    [Benchmark]
    public bool ParsePrefixedGuidUtf8() => TypedIdCodec.TryParse(_prefixedGuidUtf8, Utf8Prefix, out Guid _);

    [Benchmark]
    public bool ParsePrefixedInt64Chars() => TypedIdCodec.TryParse(_prefixedLong, Prefix, out long _);

    [Benchmark]
    public string IdToString() => _id.ToString();

    [Benchmark]
    public static Guid UuidV7Next() => UuidV7Generator.Shared.Next();

    [Benchmark]
    public static Guid UuidV4Next() => UuidV4Generator.Next();

    [Benchmark]
    public long Int64OrderedNext() => _int64.Next();

#pragma warning disable RS0030 // Bare Guids are banned in the product; these two are the references we compare against.
    [Benchmark]
    public static Guid BclNewGuid() => Guid.NewGuid();

    [Benchmark]
    public static Guid BclCreateVersion7() => Guid.CreateVersion7();
#pragma warning restore RS0030

    [Benchmark]
    public string BclGuidToString() => _guid.ToString();

    [Benchmark]
    public bool BclGuidTryParse() => Guid.TryParse(_guidText, out _);
}
