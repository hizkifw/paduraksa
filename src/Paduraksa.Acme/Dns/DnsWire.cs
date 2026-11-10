using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Paduraksa.Acme.Dns;

/// <summary>The record types the propagation check asks for or reads.</summary>
internal enum DnsRecordType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    TXT = 16,
    AAAA = 28,
}

/// <summary>One answer record. Only the fields of the types the check reads are filled: an address for A and AAAA, a target name for NS and CNAME, the joined text for TXT.</summary>
internal sealed record DnsRecord(string Name, ushort Type, IPAddress? Address, string? Target, string? Text);

/// <summary>A parsed DNS response: the header facts the check needs and the answer section. Authority and additional sections are not read.</summary>
internal sealed record DnsResponse(
    ushort Id,
    bool Truncated,
    bool Authoritative,
    int ResponseCode,
    string QuestionName,
    ushort QuestionType,
    IReadOnlyList<DnsRecord> Answers);

/// <summary>A DNS message that is malformed, or not the answer to the question asked.</summary>
internal sealed class DnsFormatException(string message) : Exception(message);

/// <summary>
/// A minimal RFC 1035 message codec (names are returned without the trailing dot, the root as <c>.</c>) for what the propagation check needs: one question out, the header and the answer section
/// back. Parsing is bounded: a datagram or stream message is at most 65535 bytes, a name at most 255 bytes of labels of at most
/// 63, compression pointers must point strictly backward (so they cannot loop) and are followed at most 32 times, every length is
/// checked against the message and the record's own RDLENGTH, and at most <see cref="MaxAnswers"/> answers are read. Anything
/// else is a <see cref="DnsFormatException"/>.
/// </summary>
internal static class DnsWire
{
    public const int MaxAnswers = 256;

    /// <summary>The EDNS0 payload size advertised: 1232 bytes avoids IP fragmentation (DNS flag day 2020).</summary>
    public const int EdnsPayloadSize = 1232;

    private const int HeaderLength = 12;
    private const int MaxNameLength = 255;
    private const int MaxPointerJumps = 32;
    private const ushort ClassIn = 1;
    private const ushort TypeOpt = 41;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>A query for one name and type, with an EDNS0 OPT record (unless <paramref name="edns"/> is false) so that a UDP answer may be larger than 512 bytes.</summary>
    public static byte[] BuildQuery(ushort id, string name, DnsRecordType type, bool recursionDesired, bool edns = true)
    {
        List<byte> message = new(64);
        message.AddRange(stackalloc byte[2] { (byte)(id >> 8), (byte)id });
        message.Add((byte)(recursionDesired ? 0x01 : 0x00));
        message.Add(0x00);
        message.AddRange(stackalloc byte[8] { 0, 1, 0, 0, 0, 0, 0, (byte)(edns ? 1 : 0) });

        var nameLength = 1;
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Latin1.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"'{name}' is not a DNS name.", nameof(name));
            }

            nameLength += bytes.Length + 1;
            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }

        if (nameLength > MaxNameLength)
        {
            throw new ArgumentException($"'{name}' is not a DNS name.", nameof(name));
        }

        message.Add(0);
        AddUInt16(message, (ushort)type);
        AddUInt16(message, ClassIn);

        if (edns)
        {
            // OPT: root name, type 41, the payload size in the class field, extended rcode/version/flags 0, no options.
            message.Add(0);
            AddUInt16(message, TypeOpt);
            AddUInt16(message, EdnsPayloadSize);
            message.AddRange(stackalloc byte[6] { 0, 0, 0, 0, 0, 0 });
        }

        return [.. message];
    }

    /// <summary>Parses a response.</summary>
    /// <exception cref="DnsFormatException">The message is not a well-formed DNS response with exactly one question.</exception>
    public static DnsResponse ParseResponse(ReadOnlySpan<byte> message)
    {
        if (message.Length is < HeaderLength or > ushort.MaxValue)
        {
            throw new DnsFormatException("The message is shorter than a DNS header or longer than 65535 bytes.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) == 0)
        {
            throw new DnsFormatException("The message is a query, not a response.");
        }

        if (((flags >> 11) & 0xF) != 0)
        {
            throw new DnsFormatException("The response is not to a standard query.");
        }

        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        if (questions != 1)
        {
            throw new DnsFormatException("The response does not echo exactly one question.");
        }

        if (answers > MaxAnswers)
        {
            throw new DnsFormatException($"The response has more than {MaxAnswers} answers.");
        }

        var position = HeaderLength;
        var questionName = ReadName(message, ref position);
        Require(message, position, 4);
        var questionType = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
        var questionClass = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]);
        position += 4;
        if (questionClass != ClassIn)
        {
            throw new DnsFormatException("The question is not in class IN.");
        }

        List<DnsRecord> records = new(answers);
        for (var i = 0; i < answers; i++)
        {
            var name = ReadName(message, ref position);
            Require(message, position, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 8)..]);
            position += 10;
            Require(message, position, length);
            var end = position + length;
            if (recordClass == ClassIn)
            {
                records.Add(ReadRecord(message, name, type, position, end));
            }

            position = end;
        }

        return new DnsResponse(id, (flags & 0x0200) != 0, (flags & 0x0400) != 0, flags & 0xF, questionName, questionType, records);
    }

    private static DnsRecord ReadRecord(ReadOnlySpan<byte> message, string name, ushort type, int start, int end)
    {
        var data = message[start..end];
        switch ((DnsRecordType)type)
        {
            case DnsRecordType.A:
                return data.Length == 4 ? new DnsRecord(name, type, new IPAddress(data), null, null) : throw new DnsFormatException("An A record is not 4 bytes.");
            case DnsRecordType.AAAA:
                return data.Length == 16 ? new DnsRecord(name, type, new IPAddress(data), null, null) : throw new DnsFormatException("An AAAA record is not 16 bytes.");
            case DnsRecordType.NS:
            case DnsRecordType.CNAME:
                {
                    var position = start;
                    var target = ReadName(message, ref position);
                    return position <= end ? new DnsRecord(name, type, null, target, null) : throw new DnsFormatException("A name runs past its record.");
                }

            case DnsRecordType.TXT:
                {
                    StringBuilder text = new();
                    var position = 0;
                    while (position < data.Length)
                    {
                        int length = data[position++];
                        if (position + length > data.Length)
                        {
                            throw new DnsFormatException("A TXT string runs past its record.");
                        }

                        text.Append(Latin1.GetString(data.Slice(position, length)));
                        position += length;
                    }

                    return new DnsRecord(name, type, null, null, text.ToString());
                }

            default:
                return new DnsRecord(name, type, null, null, null);
        }
    }

    /// <summary>
    /// Reads a (possibly compressed) name at <paramref name="position"/> and moves it past the name as it sits there: past the
    /// terminating zero, or past the first compression pointer.
    /// </summary>
    private static string ReadName(ReadOnlySpan<byte> message, ref int position)
    {
        StringBuilder name = new();
        var cursor = position;
        var resume = -1;
        var jumps = 0;
        while (true)
        {
            Require(message, cursor, 1);
            int length = message[cursor];
            if (length == 0)
            {
                position = resume >= 0 ? resume : cursor + 1;
                return name.Length == 0 ? "." : name.ToString(0, name.Length - 1);
            }

            if ((length & 0xC0) == 0xC0)
            {
                Require(message, cursor, 2);
                var target = ((length & 0x3F) << 8) | message[cursor + 1];
                if (target >= cursor || target < HeaderLength)
                {
                    throw new DnsFormatException("A compression pointer does not point backward into the message body.");
                }

                if (++jumps > MaxPointerJumps)
                {
                    throw new DnsFormatException("Too many compression pointers.");
                }

                resume = resume >= 0 ? resume : cursor + 2;
                cursor = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new DnsFormatException("A label has a reserved length type.");
            }

            Require(message, cursor + 1, length);
            if (name.Length + length + 1 > MaxNameLength)
            {
                throw new DnsFormatException("A name is longer than 255 bytes.");
            }

            foreach (var b in message.Slice(cursor + 1, length))
            {
                if (b is <= 0x20 or >= 0x7F or (byte)'.')
                {
                    throw new DnsFormatException("A label holds a byte this client does not accept.");
                }

                name.Append((char)b);
            }

            name.Append('.');
            cursor += 1 + length;
        }
    }

    private static void Require(ReadOnlySpan<byte> message, int position, int count)
    {
        if (position < 0 || count < 0 || position > message.Length - count)
        {
            throw new DnsFormatException("The message ends early.");
        }
    }

    private static void AddUInt16(List<byte> message, ushort value)
    {
        message.Add((byte)(value >> 8));
        message.Add((byte)value);
    }
}
