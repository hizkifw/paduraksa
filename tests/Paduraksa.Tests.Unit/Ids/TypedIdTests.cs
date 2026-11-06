using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Ids;

public sealed class TypedIdTests
{
    [Fact]
    public void StaticContractDescribesEachKind()
    {
        Assert.Equal(("host", IdKind.UuidV7, 27), Describe<HostId>());
        Assert.Equal(("chl", IdKind.UuidV4, 26), Describe<ChallengeId>());
        Assert.Equal(("aud", IdKind.Int64Ordered, 15), Describe<AuditEventId>());
        Assert.Equal(("usr2fa", IdKind.UuidV7, 29), Describe<InternalId>());
    }

    [Fact]
    public void RoundTripAllKinds()
    {
        for (int i = 0; i < 200; i++)
        {
            AssertRoundTrip(HostId.New());
            AssertRoundTrip(ChallengeId.New());
            AssertRoundTrip(AuditEventId.New());
            AssertRoundTrip(InternalId.New());
        }

        AssertRoundTrip(HostId.Empty);
        AssertRoundTrip(AuditEventId.FromValue(long.MaxValue));
        AssertRoundTrip(HostId.FromValue(Base62.FromUInt128(UInt128.MaxValue)));
    }

    [Fact]
    public void EmptyIsDefaultAndAllZeros()
    {
        Assert.True(HostId.Empty.IsEmpty);
        Assert.True(default(AuditEventId).IsEmpty);
        Assert.Equal("host_" + new string('0', 22), HostId.Empty.ToString());
        Assert.Equal("aud_" + new string('0', 11), AuditEventId.Empty.ToString());
        Assert.False(HostId.New().IsEmpty);
        Assert.False(ChallengeId.New().IsEmpty);
        Assert.False(AuditEventId.New().IsEmpty);
    }

    [Fact]
    public void ValueRoundTripsThroughFromValue()
    {
        HostId id = HostId.New();
        Assert.Equal(id, HostId.FromValue(id.Value));
        Assert.Equal(id, new HostId(id.Value));
        Assert.Equal(7, (int)((Base62.ToUInt128(id.Value) >> 76) & 0xF)); // UUID version nibble
    }

    [Theory]
    [InlineData("usr_0000000000000000000000")] // other prefix, same width
    [InlineData("HOST_0000000000000000000000")] // prefix is case-sensitive
    [InlineData("host-0000000000000000000000")] // wrong separator
    [InlineData("host0000000000000000000000")] // no separator
    [InlineData("hos_00000000000000000000000")] // truncated prefix
    [InlineData("host_000000000000000000000")] // 21 chars
    [InlineData("host_00000000000000000000000")] // 23 chars
    [InlineData("host_00000000000-0000000000")] // bad char
    [InlineData("host_0000000000 00000000000")] // space
    [InlineData("host_zzzzzzzzzzzzzzzzzzzzzz")] // overflow
    [InlineData(" host_0000000000000000000000")] // leading whitespace
    [InlineData("host_")]
    [InlineData("")]
    public void ParseRejectsInvalidHostIds(string input)
    {
        Assert.False(HostId.TryParse(input, out HostId result));
        Assert.True(result.IsEmpty);
        Assert.False(HostId.TryParse(Encoding.UTF8.GetBytes(input), out _));
        Assert.Throws<FormatException>(() => HostId.Parse(input));
        Assert.Throws<FormatException>(() => HostId.Parse(Encoding.UTF8.GetBytes(input).AsSpan()));
    }

    [Theory]
    [InlineData("aud_0000000000")]
    [InlineData("aud_000000000000")]
    [InlineData("aud_zzzzzzzzzzz")]
    [InlineData("chl_00000000000")]
    [InlineData("host_00000000000")]
    public void ParseRejectsInvalidInt64Ids(string input) => Assert.False(AuditEventId.TryParse(input, out _));

    [Fact]
    public void ParseRejectsOtherIdTypesString()
    {
        Assert.False(ChallengeId.TryParse(HostId.New().ToString(), out _));
        Assert.False(HostId.TryParse(ChallengeId.New().ToString(), out _));
        Assert.False(HostId.TryParse(AuditEventId.New().ToString(), out _));
    }

    [Fact]
    public void ParseNullString()
    {
        Assert.False(HostId.TryParse((string?)null, out _));
        Assert.Throws<ArgumentNullException>(() => HostId.Parse((string)null!));
    }

    [Fact]
    public void FormatFailsWhenDestinationTooSmall()
    {
        HostId id = HostId.New();
        Assert.False(id.TryFormat(new char[26], out int written));
        Assert.Equal(0, written);
        Assert.False(id.TryFormat(new byte[26], out written));
        Assert.True(id.TryFormat(new char[40], out written));
        Assert.Equal(27, written);
    }

    [Fact]
    public void FormattingIgnoresFormatAndProvider()
    {
        HostId id = HostId.New();
        Assert.Equal(id.ToString(), id.ToString("N", NumberFormatInfo.InvariantInfo));
        Assert.Equal(id.ToString(), $"{id}");
        Assert.Equal(id, HostId.Parse(id.ToString(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UuidV7CreatedInSequenceSortAsStrings()
    {
        HostId[] ids = [.. Enumerable.Range(0, 20_000).Select(_ => HostId.New())];
        for (int i = 1; i < ids.Length; i++)
        {
            Assert.True(string.CompareOrdinal(ids[i - 1].ToString(), ids[i].ToString()) < 0);
            Assert.True(ids[i - 1] < ids[i]);
        }
    }

    [Fact]
    public void Int64OrderedCreatedInSequenceSortAsStrings()
    {
        AuditEventId[] ids = [.. Enumerable.Range(0, 20_000).Select(_ => AuditEventId.New())];
        for (int i = 1; i < ids.Length; i++)
        {
            Assert.True(string.CompareOrdinal(ids[i - 1].ToString(), ids[i].ToString()) < 0);
            Assert.True(ids[i - 1].Value < ids[i].Value);
        }
    }

    [Fact]
    public void CompareToIsConsistentWithOrdinalStringOrder()
    {
        var random = new Random(1);
        ChallengeId[] guids = [.. Enumerable.Range(0, 2000).Select(_ => ChallengeId.New())];
        Assert.Equal(guids.Select(g => g.ToString()).Order(StringComparer.Ordinal), guids.Order().Select(g => g.ToString()));

        AuditEventId[] longs = [.. Enumerable.Range(0, 2000).Select(_ => AuditEventId.FromValue(random.NextInt64(0, long.MaxValue)))];
        Assert.Equal(longs.Select(g => g.ToString()).Order(StringComparer.Ordinal), longs.Order().Select(g => g.ToString()));

        HostId a = HostId.New();
        HostId b = HostId.New();
        HostId aCopy = HostId.FromValue(a.Value);
        Assert.True(a < b && a <= b && b > a && b >= a && a <= aCopy && a >= aCopy && a != b);
    }

    [Fact]
    public void JsonRoundTrip()
    {
        var dto = new Dto(HostId.New(), AuditEventId.New(), new Dictionary<ChallengeId, int> { [ChallengeId.New()] = 1 });
        string json = JsonSerializer.Serialize(dto);

        Assert.Contains($"\"{dto.Host}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{dto.Audit}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{dto.Map.Keys.Single()}\":1", json, StringComparison.Ordinal);

        Dto? back = JsonSerializer.Deserialize<Dto>(json);
        Assert.NotNull(back);
        Assert.Equal(dto.Host, back.Host);
        Assert.Equal(dto.Audit, back.Audit);
        Assert.Equal(dto.Map, back.Map);
    }

    [Fact]
    public void JsonReadsEscapedStrings()
    {
        HostId id = HostId.New();
        string escaped = "\"\\u0068" + id.ToString()[1..] + "\"";
        Assert.Equal(id, JsonSerializer.Deserialize<HostId>(escaped));
    }

    [Theory]
    [InlineData("\"usr_0000000000000000000000\"")]
    [InlineData("\"host_000\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"\\u0068ost_00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\"")]
    public void JsonRejectsInvalid(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HostId>(json));

    [Fact]
    public void TypeConverterConvertsToAndFromString()
    {
        TypeConverter converter = TypeDescriptor.GetConverter(typeof(HostId));
        Assert.IsType<TypedIdTypeConverter<HostId>>(converter);

        HostId id = HostId.New();
        Assert.True(converter.CanConvertFrom(typeof(string)));
        Assert.True(converter.CanConvertTo(typeof(string)));
        Assert.Equal(id, converter.ConvertFromInvariantString(id.ToString()));
        Assert.Equal(id.ToString(), converter.ConvertToInvariantString(id));
        Assert.True(converter.IsValid(id.ToString()));
        Assert.False(converter.IsValid("host_nope"));
        Assert.Throws<FormatException>(() => converter.ConvertFromInvariantString("host_nope"));

        TypeConverter audit = TypeDescriptor.GetConverter(typeof(AuditEventId));
        AuditEventId auditId = AuditEventId.New();
        Assert.Equal(auditId, audit.ConvertFromInvariantString(auditId.ToString()));
    }

    private static (string Prefix, IdKind Kind, int Length) Describe<T>()
        where T : struct, ITypedId<T> => (T.Prefix, T.Kind, T.FormattedLength);

    private static void AssertRoundTrip<T>(T id)
        where T : struct, ITypedId<T>
    {
        string s = id.ToString()!;
        Assert.Equal(T.FormattedLength, s.Length);
        Assert.StartsWith(T.Prefix + "_", s, StringComparison.Ordinal);
        Assert.Equal(id, T.Parse(s.AsSpan()));
        Assert.Equal(id, T.Parse(s, null));
        Assert.True(T.TryParse(s.AsSpan(), out T parsed));
        Assert.Equal(id, parsed);

        Span<char> chars = stackalloc char[64];
        Assert.True(id.TryFormat(chars, out int charsWritten, default, null));
        Assert.Equal(s, chars[..charsWritten].ToString());

        Span<byte> utf8 = stackalloc byte[64];
        Assert.True(id.TryFormat(utf8, out int bytesWritten, default, null));
        Assert.Equal(s, Encoding.UTF8.GetString(utf8[..bytesWritten]));
        Assert.True(T.TryParse(utf8[..bytesWritten], out parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(id, T.Parse(utf8[..bytesWritten], null));
    }

    [Fact]
    public void Int64RejectsValuesAboveLongMaxValue()
    {
        string max = "aud_" + Base62.Encode((ulong)long.MaxValue);
        Assert.True(AuditEventId.TryParse(max, out AuditEventId parsed));
        Assert.Equal(long.MaxValue, parsed.Value);

        Assert.False(AuditEventId.TryParse("aud_" + Base62.Encode((ulong)long.MaxValue + 1), out _));
        Assert.False(AuditEventId.TryParse("aud_" + Base62.Encode(ulong.MaxValue), out _));
        Assert.False(AuditEventId.TryParse("aud_LygHa16AHYF"u8, out _));
    }

    [Fact]
    public void Int64RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditEventId.FromValue(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditEventId(long.MinValue));
    }

    [Fact]
    public void JsonRoundTripWithSourceGeneratedContext()
    {
        var dto = new TypedIdJsonDto(HostId.New(), AuditEventId.New(), new Dictionary<ChallengeId, int> { [ChallengeId.New()] = 1 });
        string json = JsonSerializer.Serialize(dto, TypedIdJsonContext.Default.TypedIdJsonDto);

        Assert.Contains($"\"{dto.Host}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{dto.Map.Keys.Single()}\":1", json, StringComparison.Ordinal);

        TypedIdJsonDto? back = JsonSerializer.Deserialize(json, TypedIdJsonContext.Default.TypedIdJsonDto);
        Assert.NotNull(back);
        Assert.Equal(dto.Host, back.Host);
        Assert.Equal(dto.Audit, back.Audit);
        Assert.Equal(dto.Map, back.Map);
    }

    [Fact]
    public void ParseFuzzNeverThrowsAndAcceptedValuesRoundTrip()
    {
        var random = new Random(12345);
        string[] seeds = [HostId.New().ToString(), ChallengeId.New().ToString(), AuditEventId.New().ToString()];
        const string Noise = Base62.Alphabet + "_-:/ \0\u00e9\u0130zZ09";

        for (int i = 0; i < 20_000; i++)
        {
            string input = (i % 3) switch
            {
                // Mutate a valid ID: replace, insert or delete one char.
                0 => Mutate(seeds[random.Next(seeds.Length)], random, Noise),
                // Valid prefix + random tail of random length.
                1 => seeds[random.Next(seeds.Length)][..(random.Next(3, 6))] + RandomString(random, Noise, random.Next(0, 25)),
                _ => RandomString(random, Noise, random.Next(0, 35)),
            };

            FuzzOne<HostId>(input);
            FuzzOne<ChallengeId>(input);
            FuzzOne<AuditEventId>(input);

            byte[] bytes = new byte[random.Next(0, 35)];
            random.NextBytes(bytes);
            if (HostId.TryParse(bytes, out HostId fromBytes))
            {
                Assert.Equal(bytes, Encoding.UTF8.GetBytes(fromBytes.ToString()));
            }
        }
    }

    private static void FuzzOne<T>(string input)
        where T : struct, ITypedId<T>
    {
        if (T.TryParse(input.AsSpan(), out T id))
        {
            Assert.Equal(input, id.ToString()); // canonical: exactly one string per value
        }

        if (T.TryParse(Encoding.UTF8.GetBytes(input), out T utf8Id))
        {
            Assert.Equal(input, utf8Id.ToString());
        }
    }

    private static string Mutate(string s, Random random, string alphabet)
    {
        int at = random.Next(s.Length);
        char c = alphabet[random.Next(alphabet.Length)];
        return random.Next(3) switch
        {
            0 => string.Concat(s.AsSpan(0, at), new string(c, 1), s.AsSpan(at + 1)),
            1 => s.Insert(at, new string(c, 1)),
            _ => s.Remove(at, 1),
        };
    }

    private static string RandomString(Random random, string alphabet, int length) =>
        string.Create(length, (random, alphabet), static (span, state) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = state.alphabet[state.random.Next(state.alphabet.Length)];
            }
        });

    private sealed record Dto(HostId Host, AuditEventId Audit, Dictionary<ChallengeId, int> Map);
}

internal sealed record TypedIdJsonDto(HostId Host, AuditEventId Audit, Dictionary<ChallengeId, int> Map);

// The STJ source generator can't see attributes added by the typed-ID generator, so contexts register the factory.
[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]
[JsonSerializable(typeof(TypedIdJsonDto))]
internal sealed partial class TypedIdJsonContext : JsonSerializerContext;
