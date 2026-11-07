using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Paduraksa.Application.Audit;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Audit;

/// <summary>The canonical event encoding is a storage format: these tests pin it byte for byte.</summary>
public sealed class AuditChainTests
{
    private static readonly UserId User = UserId.FromValue(new Guid("01960000-0000-7000-8000-000000000001"));
    private static readonly ConfigRevisionId Revision = ConfigRevisionId.FromValue(new Guid("01960000-0000-7000-8000-000000000002"));
    private static readonly HostId Host = HostId.FromValue(new Guid("01960000-0000-7000-8000-000000000003"));

    private static readonly AuditRecord Full = new(
        AuditEventId.FromValue(0x0102030405060708),
        new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(1),
        Actor.ForUser(User),
        Channel.Api,
        "config.host.update",
        Host.ToString(),
        AuditOutcome.Success,
        "192.0.2.1",
        """{"name":"example.com"}""",
        Revision,
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        default);

    private static readonly AuditRecord Minimal = new(
        AuditEventId.FromValue(1), Full.Timestamp, Actor.System, Channel.System, "system.start", null, AuditOutcome.Success,
        null, null, null, AuditChain.GenesisPrevHash, default);

    [Fact]
    public void EncodingIsTaggedLengthPrefixedFieldsInTagOrder()
    {
        byte[] expected =
        [
            .. Field(1, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]),
            .. Field(2, Int64(Full.Timestamp.UtcTicks)),
            .. Field(3, "user"),
            .. Field(4, User.ToString()),
            .. Field(6, "api"),
            .. Field(7, "config.host.update"),
            .. Field(8, Host.ToString()),
            .. Field(9, "success"),
            .. Field(10, "192.0.2.1"),
            .. Field(11, """{"name":"example.com"}"""),
            .. Field(12, Revision.ToString()),
        ];

        Assert.Equal(expected, AuditChain.Encode(Full));
    }

    [Fact]
    public void AbsentOptionalFieldsAreLeftOut()
    {
        byte[] expected =
        [
            .. Field(1, Int64(1)),
            .. Field(2, Int64(Full.Timestamp.UtcTicks)),
            .. Field(3, "system"),
            .. Field(6, "system"),
            .. Field(7, "system.start"),
            .. Field(9, "success"),
        ];

        Assert.Equal(expected, AuditChain.Encode(Minimal));
    }

    [Fact]
    public void TokenActorsEncodeTheTokenField()
    {
        var token = ApiTokenId.New();
        var encoded = AuditChain.Encode(Minimal with { Actor = Actor.ForToken(token) });

        byte[] actor = [.. Field(3, "token"), .. Field(5, token.ToString()), .. Field(6, "system")];
        Assert.True(encoded.AsSpan().IndexOf(actor) > 0);
    }

    [Fact]
    public void HashIsSha256OfPrevHashThenEncoding()
    {
        Assert.Equal(SHA256.HashData([.. Full.PrevHash.Span, .. AuditChain.Encode(Full)]), AuditChain.ComputeHash(Full));
        Assert.Equal(SHA256.HashData([.. new byte[32], .. AuditChain.Encode(Minimal)]), AuditChain.ComputeHash(Minimal));
    }

    [Fact]
    public void HashesArePinned()
    {
        // Golden values: if these change, every stored chain stops verifying. Change the encoding only by adding
        // new optional tags (see AuditChain). Computed independently from the documented format, not by this code.
        Assert.Equal("A81C2C07B96A1E2B3048D7D20F36D8B171CAE288A03CA8D737DB5E11357285F6", Convert.ToHexString(AuditChain.ComputeHash(Full)));
        Assert.Equal("D84D1C1F5B52765F7E493EA472747DC3E3AD7649EB60D877B78CBA990D3C0FD7", Convert.ToHexString(AuditChain.ComputeHash(Minimal)));
    }

    public static TheoryData<string> Mutations =>
    [
        "id", "timestamp", "actor", "channel", "action", "target", "outcome", "clientIp", "details", "revision", "prevHash",
        "targetAbsent", "detailsEmpty",
    ];

    [Theory]
    [MemberData(nameof(Mutations))]
    public void EveryFieldChangesTheHash(string mutation)
    {
        var changed = mutation switch
        {
            "id" => Full with { Id = AuditEventId.FromValue(Full.Id.Value + 1) },
            "timestamp" => Full with { Timestamp = Full.Timestamp.AddTicks(1) },
            "actor" => Full with { Actor = Actor.ForUser(UserId.New()) },
            "channel" => Full with { Channel = Channel.Mcp },
            "action" => Full with { Action = "config.host.delete" },
            "target" => Full with { Target = HostId.New().ToString() },
            "outcome" => Full with { Outcome = AuditOutcome.Denied },
            "clientIp" => Full with { ClientIp = "192.0.2.2" },
            "details" => Full with { Details = """{"name":"example.org"}""" },
            "revision" => Full with { RevisionId = ConfigRevisionId.New() },
            "prevHash" => Full with { PrevHash = new byte[32] },
            "targetAbsent" => Full with { Target = null },
            "detailsEmpty" => Full with { Details = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.NotEqual(AuditChain.ComputeHash(Full), AuditChain.ComputeHash(changed));
    }

    [Fact]
    public void MovingTextBetweenAdjacentFieldsChangesTheEncoding()
    {
        var a = Full with { ClientIp = "192.0.2.1", Details = """{"a":1}""" };
        var b = Full with { ClientIp = "192.0.2.1{", Details = "\"a\":1}" };

        Assert.NotEqual(AuditChain.Encode(a), AuditChain.Encode(b));
    }

    [Fact]
    public void TheStoredHashIsNotPartOfTheEncoding() =>
        Assert.Equal(AuditChain.ComputeHash(Full), AuditChain.ComputeHash(Full with { Hash = new byte[32] }));

    [Fact]
    public void PrevHashMustBe32Bytes() =>
        Assert.Throws<ArgumentException>(() => AuditChain.ComputeHash(Full with { PrevHash = new byte[31] }));

    [Fact]
    public void LoneSurrogatesAreRejectedRatherThanReplaced() =>
        Assert.Throws<ArgumentException>(() => AuditChain.Encode(Full with { Details = "{\"a\":\"\ud800\"}" }));

    private static byte[] Field(byte tag, string value) => Field(tag, Encoding.UTF8.GetBytes(value));

    private static byte[] Field(byte tag, byte[] value)
    {
        var field = new byte[5 + value.Length];
        field[0] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(field.AsSpan(1), (uint)value.Length);
        value.CopyTo(field, 5);
        return field;
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
